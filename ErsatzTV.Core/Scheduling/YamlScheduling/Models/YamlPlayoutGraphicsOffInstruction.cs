using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutGraphicsOffInstruction : YamlPlayoutInstruction
{
    [YamlMember(Alias = "graphics_off", ApplyNamingConventions = false)]
    public string GraphicsOff { get; set; }

    /// <summary>
    /// Seconds to fade out before removing the element.
    /// The element stays active and fades to transparent over this duration,
    /// then is removed. If 0 (default), element is removed immediately.
    /// Usage: - graphics_off: "image/OSB.yaml"
    ///          fade_out_seconds: 3
    /// </summary>
    [YamlMember(Alias = "fade_out_seconds", ApplyNamingConventions = false)]
    public double FadeOutSeconds { get; set; }
}
