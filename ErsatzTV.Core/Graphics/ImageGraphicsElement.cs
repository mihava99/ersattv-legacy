using ErsatzTV.FFmpeg.State;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

public class ImageGraphicsElement : BaseGraphicsElement
{
    public string Image { get; set; }

    [YamlMember(Alias = "opacity_percent", ApplyNamingConventions = false)]
    public int? OpacityPercent { get; set; }

    [YamlMember(Alias = "opacity_expression", ApplyNamingConventions = false)]
    public string OpacityExpression { get; set; }

    public WatermarkLocation Location { get; set; }

    [YamlMember(Alias = "horizontal_margin_percent", ApplyNamingConventions = false)]
    public double? HorizontalMarginPercent { get; set; }

    [YamlMember(Alias = "vertical_margin_percent", ApplyNamingConventions = false)]
    public double? VerticalMarginPercent { get; set; }

    [YamlMember(Alias = "location_x", ApplyNamingConventions = false)]
    public double? LocationX { get; set; }

    [YamlMember(Alias = "location_y", ApplyNamingConventions = false)]
    public double? LocationY { get; set; }

    [YamlMember(Alias = "z_index", ApplyNamingConventions = false)]
    public int? ZIndex { get; set; }

    [YamlMember(Alias = "epg_entries", ApplyNamingConventions = false)]
    public int EpgEntries { get; set; }

    public bool Scale { get; set; }

    [YamlMember(Alias = "scale_width_percent", ApplyNamingConventions = false)]
    public double? ScaleWidthPercent { get; set; }

    [YamlMember(Alias = "place_within_source_content", ApplyNamingConventions = false)]
    public bool PlaceWithinSourceContent { get; set; }

    /// <summary>
    /// When true, element_seconds counts from the very first activation and is
    /// NOT reset when a new playout item starts. Use this for channel-wide overlays
    /// (logo bugs, PG symbols) that should fade in once and stay, rather than
    /// re-animating on every music video transition.
    /// When false (default), element_seconds resets on every new playout item.
    /// </summary>
    [YamlMember(Alias = "persistent", ApplyNamingConventions = false)]
    public bool Persistent { get; set; }

    /// <summary>
    /// Seconds to fade in from transparent to full opacity on activation.
    /// Works on all schedule types. Uses element_seconds internally.
    /// </summary>
    [YamlMember(Alias = "fade_in_seconds", ApplyNamingConventions = false)]
    public double FadeInSeconds { get; set; }

    /// <summary>
    /// Seconds to fade out before the current playout item ends.
    /// Works on all schedule types via content remaining time.
    /// </summary>
    [YamlMember(Alias = "fade_out_seconds", ApplyNamingConventions = false)]
    public double FadeOutSeconds { get; set; }
}
