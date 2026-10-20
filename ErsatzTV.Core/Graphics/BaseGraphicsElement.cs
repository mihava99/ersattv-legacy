using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

public class BaseGraphicsElement
{
    public string Name { get; set; }

    [YamlIgnore]
    public string SourceFileName { get; set; }

    public string DebugName() => string.IsNullOrEmpty(Name) ? SourceFileName : Name;

    /// <summary>
    /// Database entity ID — set by GraphicsElementLoader from PlayoutItemGraphicsElement.GraphicsElementId.
    /// Used by GraphicsPersistenceStore to detect graphics_off between playout items.
    /// </summary>
    [YamlIgnore]
    public int ElementId { get; set; }
}
