namespace ErsatzTV.Infrastructure.Streaming.Graphics;

/// <summary>
/// Process-level singleton store for persistent graphics element activation times.
/// 
/// Uses the SET of active element IDs from the previous playout item to detect
/// whether graphics_off fired between items — no time-based heuristics needed.
/// 
/// Called from GraphicsElementLoader.LoadAll with the current item's element IDs:
///   GraphicsPersistenceStore.UpdateActiveElements(currentElementIds)
/// 
/// Called from ImageElement.InitializeAsync:
///   _activatedAt = GraphicsPersistenceStore.GetOrResetActivation(key, elementId)
/// </summary>
public static class GraphicsPersistenceStore
{
    // Activation times per element key (SourceFileName)
    private static readonly Dictionary<string, DateTimeOffset> Activations = new();

    // Element IDs active in the previous playout item
    // If an element ID is NOT in this set, graphics_off fired → new session
    private static System.Collections.Generic.HashSet<int> _previousActiveIds = new();

    private static readonly object Lock = new();

    /// <summary>
    /// Called by GraphicsElementLoader at the start of LoadAll for each playout item.
    /// Updates our record of which elements were active in the previous item.
    /// </summary>
    public static void UpdateActiveElements(IEnumerable<int> currentElementIds)
    {
        lock (Lock)
        {
            _previousActiveIds = new System.Collections.Generic.HashSet<int>(currentElementIds);
        }
    }

    /// <summary>
    /// Get or reset the activation time for a persistent element.
    /// elementId: the GraphicsElement database ID for this element.
    /// Returns the _activatedAt time to use for element_seconds calculation.
    /// </summary>
    public static DateTimeOffset GetOrResetActivation(string key, int elementId)
    {
        lock (Lock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Was this element active in the previous playout item?
            // If yes → graphics_off has NOT fired → same session → reuse activation time
            // If no → graphics_off fired (or first activation) → new session → fresh fade-in
            bool wasActive = _previousActiveIds.Contains(elementId);

            if (!wasActive || !Activations.TryGetValue(key, out DateTimeOffset activatedAt))
            {
                activatedAt = now;
                Activations[key] = activatedAt;
            }

            return activatedAt;
        }
    }

    public static void Clear(string key)
    {
        lock (Lock)
        {
            Activations.Remove(key);
        }
    }
}
