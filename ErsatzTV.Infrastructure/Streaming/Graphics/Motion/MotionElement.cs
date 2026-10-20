using System.Collections.Generic;
using System.Buffers;
using System.IO.Pipelines;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using Microsoft.Extensions.Logging;
using NCalc;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class MotionElement(
    MotionGraphicsElement motionElement,
    Option<string> ffprobePath,
    Option<string> ffmpegPath,
    ILocalStatisticsProvider localStatisticsProvider,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private CancellationTokenSource _cancellationTokenSource;
    private CommandTask<CommandResult> _commandTask;
    private int _frameSize;
    private long _framesToSkip;
    private bool _hasFrame;
    private PipeReader _pipeReader;
    private SKPointI _point;
    private SKBitmap _motionFrameBitmap;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private MotionElementState _state;

    // element_seconds, tags, block_remaining support

    private static float ComputeFadeOpacity(
        double elementSeconds,
        double contentRemainingSeconds,
        double fadeInSeconds,
        double fadeOutSeconds,
        float baseOpacity)
    {
        float opacity = baseOpacity;
        if (fadeInSeconds > 0 && elementSeconds < fadeInSeconds)
            opacity = baseOpacity * (float)(elementSeconds / fadeInSeconds);
        if (fadeOutSeconds > 0 && contentRemainingSeconds < fadeOutSeconds)
        {
            float fadeOutFactor = (float)(contentRemainingSeconds / fadeOutSeconds);
            opacity = Math.Min(opacity, baseOpacity * fadeOutFactor);
        }
        return Math.Clamp(opacity, 0f, 1f);
    }
    private static readonly Dictionary<string, DateTimeOffset> PersistentActivations = new();
    private static readonly object PersistentLock = new();
    private DateTimeOffset _activatedAt;
    private float _baseOpacity = 1.0f;
    private Option<Expression> _maybeOpacityExpression;
    private IReadOnlyList<string> _tags = [];
    private double _blockRemainingSeconds;

    public override int ZIndex { get; } = motionElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Motion {motionElement.DebugName()}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _pipeReader?.Complete();

        _cancellationTokenSource?.Cancel();
        try
        {
#pragma warning disable VSTHRD002
            _commandTask?.Task.Wait();
#pragma warning restore VSTHRD002
        }
        catch (Exception)
        {
            // do nothing
        }

        _cancellationTokenSource?.Dispose();

        _motionFrameBitmap?.Dispose();
    }

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Set up opacity expression if provided
            if (!string.IsNullOrWhiteSpace(motionElement.OpacityExpression))
            {
                var expression = new Expression(motionElement.OpacityExpression);
                expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
                _maybeOpacityExpression = expression;
            }

            _startTime = TimeSpan.FromSeconds(motionElement.StartSeconds ?? 0);
            var holdDuration = TimeSpan.FromSeconds(motionElement.HoldSeconds ?? 0);
            ProbeResult probeResult = await ProbeMotionElement(context.FrameSize);
            var overlayDuration = motionElement.EndBehavior switch
            {
                MotionEndBehavior.Loop => context.Seek + context.Duration - _startTime,
                MotionEndBehavior.Hold => probeResult.Duration + holdDuration,
                // HoldIndefinitely: run the video once, then hold last frame until disposed
                MotionEndBehavior.HoldIndefinitely => probeResult.Duration,
                _ => probeResult.Duration
            };

            _endTime = _startTime + overlayDuration;

            // already past the time when this is supposed to play; don't do any more work
            if (_endTime <= context.Seek || _startTime >= context.Seek + context.Duration)
            {
                IsFinished = true;
                return;
            }

            var pipe = new Pipe();
            _pipeReader = pipe.Reader;

            var overlaySeekTime = TimeSpan.Zero;
            if (_startTime < context.Seek)
            {
                overlaySeekTime = context.Seek - _startTime;
            }

            if (motionElement.EndBehavior is MotionEndBehavior.Hold)
            {
                // seek over the prefix, but leave a second at EOF to recover the final
                // frame reliably. seeking too close to EOF can produce no frames.
                TimeSpan latestSeek = probeResult.Duration > TimeSpan.FromSeconds(1)
                    ? probeResult.Duration - TimeSpan.FromSeconds(1)
                    : TimeSpan.Zero;
                TimeSpan inputSeek = overlaySeekTime < latestSeek ? overlaySeekTime : latestSeek;
                _framesToSkip = (long)((overlaySeekTime - inputSeek).TotalSeconds *
                    context.FrameRate.ParsedFrameRate);
                overlaySeekTime = inputSeek;
            }
            else if (motionElement.EndBehavior is MotionEndBehavior.Loop && probeResult.Duration > TimeSpan.Zero)
            {
                overlaySeekTime = TimeSpan.FromTicks(overlaySeekTime.Ticks % probeResult.Duration.Ticks);
            }

            Resolution sourceSize = probeResult.Size;

            int scaledWidth = sourceSize.Width;
            int scaledHeight = sourceSize.Height;

            if (motionElement.Scale)
            {
                scaledWidth = (int)Math.Round(
                    (motionElement.ScaleWidthPercent ?? 100) / 100.0 * context.FrameSize.Width);
                double aspectRatio = (double)sourceSize.Height / sourceSize.Width;
                scaledHeight = (int)Math.Round(scaledWidth * aspectRatio);
            }

            // ensure even dimensions
            if (scaledWidth % 2 != 0) scaledWidth++;
            if (scaledHeight % 2 != 0) scaledHeight++;

            var targetSize = new Resolution { Width = scaledWidth, Height = scaledHeight };

            _frameSize = targetSize.Width * targetSize.Height * 4;

            _motionFrameBitmap = new SKBitmap(
                targetSize.Width,
                targetSize.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul);

            _point = SKPointI.Empty;

            (int horizontalMargin, int verticalMargin) = NormalMargins(
                context.FrameSize,
                motionElement.HorizontalMarginPercent ?? 0,
                motionElement.VerticalMarginPercent ?? 0);

            _point = CalculatePosition(
                motionElement.Location,
                context.FrameSize.Width,
                context.FrameSize.Height,
                targetSize.Width,
                targetSize.Height,
                horizontalMargin,
                verticalMargin);

            List<string> arguments = ["-nostdin", "-hide_banner", "-nostats", "-loglevel", "error"];

            if (motionElement.EndBehavior is MotionEndBehavior.Loop)
            {
                arguments.AddRange(["-stream_loop", "-1"]);
            }

            foreach (string decoder in probeResult.Decoder)
            {
                arguments.AddRange(["-c:v", decoder]);
            }

            if (overlaySeekTime > TimeSpan.Zero && motionElement.EndBehavior is not MotionEndBehavior.Loop)
            {
                arguments.AddRange(["-ss", FFmpegFormatter.Milliseconds(overlaySeekTime)]);
            }

            arguments.AddRange(["-i", motionElement.VideoPath]);

            // output seeking preserves the complete source on subsequent loop iterations.
            if (overlaySeekTime > TimeSpan.Zero && motionElement.EndBehavior is MotionEndBehavior.Loop)
            {
                arguments.AddRange(["-ss", FFmpegFormatter.Milliseconds(overlaySeekTime)]);
            }

            var videoFilter = $"fps={context.FrameRate.RFrameRate}";
            if (motionElement.Scale)
            {
                videoFilter += $",scale={targetSize.Width}:{targetSize.Height}";
            }

            arguments.AddRange(["-vf", videoFilter]);

            if (motionElement.EndBehavior is MotionEndBehavior.Loop)
            {
                TimeSpan playbackStart = context.Seek > _startTime ? context.Seek : _startTime;
                arguments.AddRange(["-t", FFmpegFormatter.Milliseconds(_endTime - playbackStart)]);
            }

            arguments.AddRange(
            [
                "-f", "image2pipe",
                "-pix_fmt", "bgra",
                "-vcodec", "rawvideo",
                "-"
            ]);

            _state = MotionElementState.PlayingIn;

            Command command = Cli.Wrap(await ffmpegPath.IfNoneAsync("ffmpeg"))
                .WithArguments(arguments)
                .WithWorkingDirectory(FileSystemLayout.TempFilePoolFolder)
                .WithStandardOutputPipe(PipeTarget.ToStream(pipe.Writer.AsStream()));

            logger.LogDebug("ffmpeg motion element arguments {FFmpegArguments}", command.Arguments);

            _cancellationTokenSource = new CancellationTokenSource();
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellationTokenSource.Token);

            _commandTask = command.ExecuteAsync(linkedToken.Token);

            _ = _commandTask.Task.ContinueWith(_ => pipe.Writer.Complete(), TaskScheduler.Default);

            // Record activation time — persistent: true keeps it across playout items
            if (motionElement.Persistent)
            {
                string key = motionElement.SourceFileName ?? motionElement.DebugName();
                lock (PersistentLock)
                {
                    if (!PersistentActivations.TryGetValue(key, out _activatedAt))
                    {
                        _activatedAt = DateTimeOffset.UtcNow;
                        PersistentActivations[key] = _activatedAt;
                    }
                }
            }
            else
            {
                _activatedAt = DateTimeOffset.UtcNow;
            }
            _blockRemainingSeconds = context.BlockRemainingTime.TotalSeconds;
            if (context.TemplateVariables.TryGetValue("MediaItem_Tags", out object tagsObj) &&
                tagsObj is IEnumerable<string> tagList)
            {
                _tags = tagList.ToList();
            }
        }
        catch (Exception ex)
        {
            IsFinished = true;
            logger.LogWarning(ex, "Failed to initialize motion element; will disable for this content");
        }
    }

    public override async ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IsFinished || _state is MotionElementState.Finished || contentTime < _startTime)
            {
                return Option<PreparedElementImage>.None;
            }

            // ── HoldIndefinitely: hold last frame until disposed (graphics_off) ──────
            // Unlike Hold (which has a fixed _endTime), HoldIndefinitely never sets
            // IsFinished — the element stays alive until the graphics engine disposes
            // it when graphics_off fires in the sequential schedule.
            // An optional opacity_expression (using element_seconds) can be used to
            // fade in and/or fade out without knowing the hold duration in advance.
            if (motionElement.EndBehavior is MotionEndBehavior.HoldIndefinitely)
            {
                if (_state is MotionElementState.Holding)
                {
                    float holdOpacity = GetCurrentOpacity(timeOfDay, contentTime, contentTotalTime, channelTime);
                    return holdOpacity == 0
                        ? Option<PreparedElementImage>.None
                        : new PreparedElementImage(_motionFrameBitmap, _point, holdOpacity, ZIndex, false);
                }
            }
            else if (contentTime >= _endTime)
            {
                IsFinished = true;
                _state = MotionElementState.Finished;
                return Option<PreparedElementImage>.None;
            }

            if (_state is MotionElementState.Holding)
            {
                return new PreparedElementImage(_motionFrameBitmap, _point, 1.0f, ZIndex, false);
            }

            while (true)
            {
                ReadResult readResult = await _pipeReader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = readResult.Buffer;
                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                try
                {
                    if (buffer.Length >= _frameSize)
                    {
                        ReadOnlySequence<byte> sequence = buffer.Slice(0, _frameSize);

                        using (SKPixmap pixmap = _motionFrameBitmap.PeekPixels())
                        {
                            sequence.CopyTo(pixmap.GetPixelSpan());
                        }

                        consumed = sequence.End;
                        examined = consumed;
                        _hasFrame = true;

                        if (_framesToSkip > 0)
                        {
                            _framesToSkip--;
                            continue;
                        }

                        float opacity = GetCurrentOpacity(timeOfDay, contentTime, contentTotalTime, channelTime);
                        return opacity == 0
                            ? Option<PreparedElementImage>.None
                            : new PreparedElementImage(_motionFrameBitmap, _point, opacity, ZIndex, false);
                    }

                    if (readResult.IsCompleted)
                    {
                        await _pipeReader.CompleteAsync();

                        if (motionElement.EndBehavior is MotionEndBehavior.Hold && _hasFrame)
                        {
                            _state = MotionElementState.Holding;
                            return new PreparedElementImage(_motionFrameBitmap, _point, 1.0f, ZIndex, false);
                        }
                        else if (motionElement.EndBehavior is MotionEndBehavior.HoldIndefinitely && _hasFrame)
                        {
                            _state = MotionElementState.Holding;
                            float opacity = GetCurrentOpacity(timeOfDay, contentTime, contentTotalTime, channelTime);
                            return opacity == 0
                                ? Option<PreparedElementImage>.None
                                : new PreparedElementImage(_motionFrameBitmap, _point, opacity, ZIndex, false);
                        }
                        else
                        {
                            IsFinished = true;
                            _state = MotionElementState.Finished;
                        }

                        return Option<PreparedElementImage>.None;
                    }
                }
                finally
                {
                    if (_state is not (MotionElementState.Finished or MotionElementState.Holding))
                    {
                        // leave unread frames available without waiting for more data
                        _pipeReader.AdvanceTo(consumed, examined);
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            return Option<PreparedElementImage>.None;
        }
    }

    /// <summary>
    /// Returns the current opacity from the opacity_expression (if set) using
    /// element_seconds, or 1.0f if no expression is configured.
    /// </summary>
    private float GetCurrentOpacity(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime)
    {
        // Check if graphics_off triggered a fade-out
        float? fadeOutOpacity = GetFadeOutOpacity(motionElement.FadeOutSeconds, _baseOpacity);
        if (fadeOutOpacity.HasValue)
        {
            if (IsFinished) return 0f;
            return fadeOutOpacity.Value;
        }

        double elemSecs = (DateTimeOffset.UtcNow - _activatedAt).TotalSeconds;
        double contentRemaining = (contentTotalTime - contentTime).TotalSeconds;

        if (_maybeOpacityExpression.IsSome)
        {
            foreach (Expression expression in _maybeOpacityExpression)
            {
                return OpacityExpressionHelper.GetOpacity(
                    expression,
                    timeOfDay,
                    contentTime,
                    contentTotalTime,
                    channelTime,
                    elemSecs,
                    _blockRemainingSeconds,
                    _tags);
            }
        }

        return ComputeFadeOpacity(
            elemSecs,
            contentRemaining,
            motionElement.FadeInSeconds,
            motionElement.FadeOutSeconds,
            _baseOpacity);
    }

    private async Task<ProbeResult> ProbeMotionElement(Resolution frameSize)
    {
        try
        {
            foreach (string ffprobe in ffprobePath)
            {
                Either<BaseError, MediaVersion> maybeMediaVersion =
                    await localStatisticsProvider.GetStatistics(ffprobe, motionElement.VideoPath);

                foreach (var mediaVersion in maybeMediaVersion.RightToSeq())
                {
                    Option<string> decoder = Option<string>.None;

                    foreach (var videoStream in mediaVersion.Streams.Where(s =>
                                 s.MediaStreamKind is MediaStreamKind.Video))
                    {
                        decoder = videoStream.Codec switch
                        {
                            "vp8" => "libvpx",
                            "vp9" => "libvpx-vp9",
                            _ => Option<string>.None
                        };
                    }

                    return new ProbeResult(
                        new Resolution { Width = mediaVersion.Width, Height = mediaVersion.Height },
                        decoder,
                        mediaVersion.Duration);
                }
            }
        }
        catch (Exception)
        {
            // do nothing
        }

        return new ProbeResult(frameSize, Option<string>.None, TimeSpan.Zero);
    }

    private record ProbeResult(Resolution Size, Option<string> Decoder, TimeSpan Duration);
}
