using System.Text.Json;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutGraphicsOffHandler(IGraphicsElementRepository graphicsElementRepository) : IYamlPlayoutHandler
{
    private readonly Dictionary<string, Option<GraphicsElement>> _graphicsElementCache = new();

    public bool Reset => false;

    public async Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutGraphicsOffInstruction graphicsOff)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(graphicsOff.GraphicsOff))
        {
            context.ClearGraphicsElements();
        }
        else
        {
            foreach (GraphicsElement ge in await GetGraphicsElementByPath(graphicsOff.GraphicsOff, cancellationToken))
            {
                // If fade_out_seconds is set, inject it into the LAST added playout item's
                // graphics element variables so the renderer fades out during that item.
                // This works on ALL schedule types since it modifies already-built items.
                if (graphicsOff.FadeOutSeconds > 0 && context.AddedItems.Count > 0)
                {
                    PlayoutItem lastItem = context.AddedItems[^1];
                    PlayoutItemGraphicsElement pige = lastItem.PlayoutItemGraphicsElements
                        .FirstOrDefault(x => x.GraphicsElementId == ge.Id);

                    if (pige is not null)
                    {
                        Dictionary<string, string> vars = new();
                        if (!string.IsNullOrWhiteSpace(pige.Variables))
                        {
                            try { vars = JsonSerializer.Deserialize<Dictionary<string, string>>(pige.Variables) ?? vars; }
                            catch { }
                        }
                        vars["fade_out_seconds"] = graphicsOff.FadeOutSeconds.ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                        pige.Variables = JsonSerializer.Serialize(vars);
                    }
                }

                context.RemoveGraphicsElement(ge.Id);
            }
        }

        return true;
    }

    private async Task<Option<GraphicsElement>> GetGraphicsElementByPath(
        string path,
        CancellationToken cancellationToken)
    {
        if (_graphicsElementCache.TryGetValue(path, out Option<GraphicsElement> cachedGraphicsElement))
        {
            foreach (GraphicsElement graphicsElement in cachedGraphicsElement)
            {
                return graphicsElement;
            }
        }
        else
        {
            Option<GraphicsElement> maybeGraphicsElement =
                await graphicsElementRepository.GetGraphicsElementByPath(path, cancellationToken);
            _graphicsElementCache.Add(path, maybeGraphicsElement);
            foreach (GraphicsElement graphicsElement in maybeGraphicsElement)
            {
                return graphicsElement;
            }
        }

        return Option<GraphicsElement>.None;
    }
}
