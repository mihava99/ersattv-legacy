using ErsatzTV.Core.Interfaces.Streaming;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public interface IGraphicsElement
{
    int ZIndex { get; }

    string DebugKey { get; }

    bool IsFinished { get; set; }

    Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken);

    ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken);

    /// <summary>
    /// Called when graphics_off fires. The element will fade out over
    /// its configured fade_out_seconds then mark itself IsFinished.
    /// If fade_out_seconds is 0, marks IsFinished immediately.
    /// </summary>
    void BeginFadeOut();
}
