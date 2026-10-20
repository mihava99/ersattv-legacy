using ErsatzTV.FFmpeg.State;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

public class MotionGraphicsElement : BaseGraphicsElement
{
    [YamlMember(Alias = "video_path", ApplyNamingConventions = false)]
    public string VideoPath { get; set; }

    [YamlMember(Alias = "opacity_expression", ApplyNamingConventions = false)]
    public string OpacityExpression { get; set; }

    /// <summary>
    /// Duration in seconds of the fade-out animation when end_behavior is
    /// HoldIndefinitely. The opacity expression (or a built-in linear fade)
    /// will be applied during this window before graphics_off disposes the element.
    /// Only used with end_behavior: HoldIndefinitely.
    /// </summary>
    [YamlMember(Alias = "start_seconds", ApplyNamingConventions = false)]
    public double? StartSeconds { get; set; }

    [YamlMember(Alias = "end_behavior", ApplyNamingConventions = false)]
    public MotionEndBehavior EndBehavior { get; set; } = MotionEndBehavior.Disappear;

    [YamlMember(Alias = "hold_seconds", ApplyNamingConventions = false)]
    public double? HoldSeconds { get; set; }

    public WatermarkLocation Location { get; set; }

    [YamlMember(Alias = "horizontal_margin_percent", ApplyNamingConventions = false)]
    public double? HorizontalMarginPercent { get; set; }

    [YamlMember(Alias = "vertical_margin_percent", ApplyNamingConventions = false)]
    public double? VerticalMarginPercent { get; set; }

    [YamlMember(Alias = "z_index", ApplyNamingConventions = false)]
    public int? ZIndex { get; set; }

    [YamlMember(Alias = "epg_entries", ApplyNamingConventions = false)]
    public int EpgEntries { get; set; }

    public bool Scale { get; set; }

    [YamlMember(Alias = "scale_width_percent", ApplyNamingConventions = false)]
    public double? ScaleWidthPercent { get; set; }

    /// <summary>
    /// When true, element_seconds is NOT reset on new playout items.
    /// </summary>
    [YamlMember(Alias = "persistent", ApplyNamingConventions = false)]
    public bool Persistent { get; set; }

    [YamlMember(Alias = "fade_in_seconds", ApplyNamingConventions = false)]
    public double FadeInSeconds { get; set; }

    [YamlMember(Alias = "fade_out_seconds", ApplyNamingConventions = false)]
    public double FadeOutSeconds { get; set; }
}
