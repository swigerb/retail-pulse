namespace RetailPulse.Api.Packs;

/// <summary>
/// Top-level shape of a pack's <c>starting-tasks.yaml</c>. Optional —
/// missing file yields an empty <see cref="Categories"/> list so a pack
/// author can add starting tasks incrementally.
/// </summary>
public sealed class PackStartingTasksDocument
{
    public List<PackStartingTaskCategory> Categories { get; init; } = [];
}

/// <summary>
/// One category of curated starting prompts. Mirrors the shape the
/// existing web PromptLibrary consumes so a downstream endpoint can
/// project this straight into the client without reshaping.
/// </summary>
public sealed class PackStartingTaskCategory
{
    /// <summary>Stable identifier used by the web PromptLibrary for
    /// category filtering and analytics keys.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable category label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Emoji icon shown in the PromptLibrary chip.</summary>
    public string Emoji { get; init; } = "";

    /// <summary>Ordered list of prompt strings shown in the category.</summary>
    public List<string> Prompts { get; init; } = [];
}
