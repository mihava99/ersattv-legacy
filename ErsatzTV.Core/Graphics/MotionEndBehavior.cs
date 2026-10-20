namespace ErsatzTV.Core.Graphics;

public enum MotionEndBehavior
{
    Disappear = 0,
    Loop = 1,
    Hold = 2,

    /// <summary>
    /// Hold the last frame indefinitely until the graphics element is turned
    /// off (graphics_off in the sequential schedule). No hold_seconds needed.
    /// Combine with opacity_expression using element_seconds for a fade-out:
    ///   e.g. fade in over 1s, hold, then fade out over 1s before graphics_off.
    /// </summary>
    HoldIndefinitely = 3
}
