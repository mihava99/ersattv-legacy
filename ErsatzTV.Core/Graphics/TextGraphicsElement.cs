using ErsatzTV.FFmpeg.State;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

public class TextGraphicsElement : BaseGraphicsElement
{
    [YamlMember(Alias = "opacity_percent", ApplyNamingConventions = false)]
    public int? OpacityPercent { get; set; }

    [YamlMember(Alias = "opacity_expression", ApplyNamingConventions = false)]
    public string OpacityExpression { get; set; }

    public WatermarkLocation Location { get; set; }

    [YamlMember(Alias = "horizontal_margin_percent", ApplyNamingConventions = false)]
    public double? HorizontalMarginPercent { get; set; }

    [YamlMember(Alias = "vertical_margin_percent", ApplyNamingConventions = false)]
    public double? VerticalMarginPercent { get; set; }

    [YamlMember(Alias = "width_percent", ApplyNamingConventions = false)]
    public double? WidthPercent { get; set; }

    [YamlMember(Alias = "text_fit", ApplyNamingConventions = false)]
    public TextFit Fit { get; set; } = TextFit.None;

    [YamlMember(Alias = "text_align", ApplyNamingConventions = false)]
    public TextAlignment Align { get; set; } = TextAlignment.Left;

    [YamlMember(Alias = "z_index", ApplyNamingConventions = false)]
    public int? ZIndex { get; set; }

    public List<StyleDefinition> Styles { get; set; } = [];

    [YamlMember(Alias = "base_style", ApplyNamingConventions = false)]
    public string BaseStyle { get; set; }

    [YamlMember(Alias = "include_fonts_from", ApplyNamingConventions = false)]
    public string IncludeFontsFrom { get; set; }

    [YamlMember(Alias = "epg_entries", ApplyNamingConventions = false)]
    public int EpgEntries { get; set; }

    public string Text { get; set; }

    /// <summary>
    /// When true, element_seconds is NOT reset on new playout items.
    /// </summary>
    [YamlMember(Alias = "persistent", ApplyNamingConventions = false)]
    public bool Persistent { get; set; }

    [YamlMember(Alias = "fade_in_seconds", ApplyNamingConventions = false)]
    public double FadeInSeconds { get; set; }

    [YamlMember(Alias = "fade_out_seconds", ApplyNamingConventions = false)]
    public double FadeOutSeconds { get; set; }

    /// <summary>
    /// Path to a plain text file. When set, the file is read and reloaded
    /// at render time whenever its mtime changes, giving live updates.
    /// Injected manually via regex in GraphicsElementLoader since
    /// CamelCaseNamingConvention cannot map text_file automatically.
    /// </summary>
    public string TextFile { get; set; }
}

public enum TextFit
{
    None,
    Wrap,
    Scale
}

public enum TextAlignment
{
    Left,
    Center,
    Right
}

public class StyleDefinition
{
    public string Name { get; set; }

    [YamlMember(Alias = "font_size", ApplyNamingConventions = false)]
    public float? FontSize { get; set; }

    [YamlMember(Alias = "font_weight", ApplyNamingConventions = false)]
    public int? FontWeight { get; set; }

    [YamlMember(Alias = "font_italic", ApplyNamingConventions = false)]
    public bool? FontItalic { get; set; }

    [YamlMember(Alias = "font_family", ApplyNamingConventions = false)]
    public string FontFamily { get; set; }

    [YamlMember(Alias = "text_color", ApplyNamingConventions = false)]
    public string TextColor { get; set; }

    [YamlMember(Alias = "letter_spacing", ApplyNamingConventions = false)]
    public float? LetterSpacing { get; set; }

    [YamlMember(Alias = "line_height", ApplyNamingConventions = false)]
    public float? LineHeight { get; set; }

    [YamlMember(Alias = "halo_color", ApplyNamingConventions = false)]
    public string HaloColor { get; set; }

    [YamlMember(Alias = "halo_width", ApplyNamingConventions = false)]
    public float? HaloWidth { get; set; }

    [YamlMember(Alias = "halo_blur", ApplyNamingConventions = false)]
    public float? HaloBlur { get; set; }
}
