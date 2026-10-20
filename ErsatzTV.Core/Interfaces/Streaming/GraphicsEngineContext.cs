using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Graphics;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Core.Interfaces.Streaming;

public record GraphicsEngineContext(
    string ChannelNumber,
    MediaItem MediaItem,
    List<GraphicsElementContext> Elements,
    Dictionary<string, object> TemplateVariables,
    Resolution SquarePixelFrameSize,
    Resolution FrameSize,
    FrameRate FrameRate,
    DateTimeOffset ChannelStartTime,
    DateTimeOffset ContentStartTime,
    DateTimeOffset ContentFinishTime,
    TimeSpan Seek,
    TimeSpan Duration,
    TimeSpan ContentTotalDuration,
    /// <summary>
    /// Time remaining until the end of the current playout block
    /// (e.g. the whole commercial break, not just the current item).
    /// Available as block_remaining_seconds in opacity_expression.
    /// Defaults to TimeSpan.Zero when not applicable.
    /// </summary>
    TimeSpan BlockRemainingTime = default,
    /// <summary>
    /// Graphics elements that have been graphics_off'd with a fade_out_seconds value.
    /// These are injected into LoadAll so they continue rendering during their fade.
    /// Key: element path, Value: (fade_out_seconds, activated_at)
    /// </summary>
    List<FadingOutElementContext> FadingOutElements = null)
{
    public Option<CanvasRegion> Region { get; init; }
}

public record FadingOutElementContext(string Path, double FadeOutSeconds, DateTimeOffset ActivatedAt);

public abstract record GraphicsElementContext;

public record WatermarkElementContext(WatermarkOptions Options) : GraphicsElementContext;

public record TextElementDataContext(TextGraphicsElement TextElement) : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => TextElement.EpgEntries;
}

public record ImageElementDataContext(ImageGraphicsElement ImageElement) : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => ImageElement.EpgEntries;
}

public record MotionElementDataContext(MotionGraphicsElement MotionElement)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => MotionElement.EpgEntries;
}

public record SubtitleElementDataContext(
    SubtitleGraphicsElement SubtitleElement,
    Dictionary<string, string> Variables)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => SubtitleElement.EpgEntries;
}

public record ScriptElementDataContext(ScriptGraphicsElement ScriptElement)
    : GraphicsElementContext, ITemplateDataContext
{
    public int EpgEntries => ScriptElement.EpgEntries;
}


public interface ITemplateDataContext
{
    int EpgEntries { get; }
}
