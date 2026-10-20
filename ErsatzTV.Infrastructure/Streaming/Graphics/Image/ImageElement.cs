using System.Collections.Generic;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using NCalc;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public class ImageElement(ImageGraphicsElement imageGraphicsElement, ILogger logger) : ImageElementBase
{
    private int _debugFrameCount;

    // Persistence handled by GraphicsPersistenceStore (dedicated static class)

    private Option<Expression> _maybeOpacityExpression;
    private float _opacity;
    private float _baseOpacity = 1.0f;
    private DateTimeOffset _activatedAt;
    private IReadOnlyList<string> _tags = [];
    private double _blockRemainingSeconds;

    public override int ZIndex { get; } = imageGraphicsElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Image {imageGraphicsElement.DebugName()}";

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(imageGraphicsElement.OpacityExpression))
            {
                var expression = new Expression(imageGraphicsElement.OpacityExpression);
                expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
                _maybeOpacityExpression = expression;
            }
            else
            {
                _opacity = (imageGraphicsElement.OpacityPercent ?? 100) / 100.0f;
            _baseOpacity = _opacity;
            }

            foreach (Expression expression in _maybeOpacityExpression)
            {
                expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
            }

            await LoadImage(
                context.SquarePixelFrameSize,
                context.FrameSize,
                imageGraphicsElement.Image,
                imageGraphicsElement.Location,
                imageGraphicsElement.Scale,
                imageGraphicsElement.ScaleWidthPercent,
                imageGraphicsElement.HorizontalMarginPercent,
                imageGraphicsElement.VerticalMarginPercent,
                imageGraphicsElement.PlaceWithinSourceContent,
                cancellationToken);

            _baseOpacity = (imageGraphicsElement.OpacityPercent ?? 100) / 100.0f;
            logger.LogDebug("Image element {Name} persistent={Persistent} fadeIn={FadeIn} fadeOut={FadeOut} baseOpacity={Base}",
                imageGraphicsElement.DebugName(),
                imageGraphicsElement.Persistent,
                imageGraphicsElement.FadeInSeconds,
                imageGraphicsElement.FadeOutSeconds,
                _baseOpacity);
            if (imageGraphicsElement.Persistent)
            {
                string key = imageGraphicsElement.SourceFileName ?? imageGraphicsElement.DebugName();
                _activatedAt = GraphicsPersistenceStore.GetOrResetActivation(key, imageGraphicsElement.ElementId);
                logger.LogDebug("Persistent image element key={Key} id={Id} elemSecs={ES}",
                    key, imageGraphicsElement.ElementId, (DateTimeOffset.UtcNow - _activatedAt).TotalSeconds);
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
            logger.LogWarning(ex, "Failed to initialize image element; will disable for this content");
        }
    }


    /// <summary>
    /// Computes opacity from fade_in_seconds / fade_out_seconds properties.
    /// fade_in: uses element_seconds (persistent-aware, counts from activation)
    /// fade_out: uses contentRemainingSeconds (time left in current item) —
    ///           works on ALL schedule types (classic, block, sequential).
    /// If opacity_expression is ALSO set, it takes precedence over this.
    /// baseOpacity: the opacity_percent value (0.0-1.0), default 1.0
    /// </summary>
    private static float ComputeFadeOpacity(
        double elementSeconds,
        double contentRemainingSeconds,
        double fadeInSeconds,
        double fadeOutSeconds,
        float baseOpacity)
    {
        float opacity = baseOpacity;

        // Fade in: ramp from 0 → baseOpacity over fadeInSeconds from activation
        if (fadeInSeconds > 0 && elementSeconds < fadeInSeconds)
        {
            opacity = baseOpacity * (float)(elementSeconds / fadeInSeconds);
        }

        // Fade out: ramp from baseOpacity → 0 during last fadeOutSeconds of item
        if (fadeOutSeconds > 0 && contentRemainingSeconds < fadeOutSeconds)
        {
            float fadeOutFactor = (float)(contentRemainingSeconds / fadeOutSeconds);
            opacity = Math.Min(opacity, baseOpacity * fadeOutFactor);
        }

        return Math.Clamp(opacity, 0f, 1f);
    }
    public override ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        // Check if graphics_off triggered a fade-out — takes priority over everything
        float? fadeOutOpacity = GetFadeOutOpacity(imageGraphicsElement.FadeOutSeconds, _baseOpacity);
        if (fadeOutOpacity.HasValue)
        {
            if (IsFinished)
                return ValueTask.FromResult(Option<PreparedElementImage>.None);
            SKBitmap fadeFrame = GetFrameForTimestamp(contentTime);
            return ValueTask.FromResult(
                Optional(new PreparedElementImage(fadeFrame, Location, fadeOutOpacity.Value, ZIndex, false)));
        }

        double elemSecs = (DateTimeOffset.UtcNow - _activatedAt).TotalSeconds;
        double contentRemaining = (contentTotalTime - contentTime).TotalSeconds;

        if (_debugFrameCount++ < 3)
        {
            logger.LogDebug("Image PrepareImage: elemSecs={E} contentRemaining={R} fadeIn={FI} fadeOut={FO} baseOpacity={B} hasFade={HF}",
                elemSecs, contentRemaining,
                imageGraphicsElement.FadeInSeconds,
                imageGraphicsElement.FadeOutSeconds,
                _baseOpacity,
                imageGraphicsElement.FadeInSeconds > 0 || imageGraphicsElement.FadeOutSeconds > 0);
        }

        float opacity;
        if (_maybeOpacityExpression.IsSome)
        {
            opacity = _opacity;
            foreach (Expression expression in _maybeOpacityExpression)
            {
                opacity = OpacityExpressionHelper.GetOpacity(
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
        else
        {
            // Use built-in fade_in/fade_out when no expression is set
            opacity = ComputeFadeOpacity(
                elemSecs,
                contentRemaining,
                imageGraphicsElement.FadeInSeconds,
                imageGraphicsElement.FadeOutSeconds,
                _baseOpacity);
        }

        if (opacity == 0)
        {
            return ValueTask.FromResult(Option<PreparedElementImage>.None);
        }

        SKBitmap frameForTimestamp = GetFrameForTimestamp(contentTime);
        return ValueTask.FromResult(
            Optional(new PreparedElementImage(frameForTimestamp, Location, opacity, ZIndex, false)));
    }
}
