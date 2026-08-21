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
/// One category of curated starting prompts. Issue #109 adds structured
/// <see cref="Tasks"/> (display name + submitted prompt + optional
/// capability metadata) alongside the legacy <see cref="Prompts"/> list
/// so a scenario can showcase specific behavior — a chart type or a
/// multi-step plan path — deliberately. Older packs that only declare
/// <see cref="Prompts"/> keep loading unchanged: the loader synthesizes
/// tasks from prompt strings and the frontend receives the same shape.
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

    /// <summary>
    /// Optional explicit ordering. Categories with a lower <see cref="Order"/>
    /// value render first; ties break on source-array position so an author
    /// can leave the field off entirely and get deterministic behavior.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Structured tasks for this category. When declared, this list is the
    /// source of truth and <see cref="Prompts"/> is derived from it. When
    /// omitted, the loader synthesizes tasks from the legacy string list so
    /// existing packs keep working without change.
    /// </summary>
    public List<PackStartingTask> Tasks { get; init; } = [];

    /// <summary>
    /// Legacy list of prompt strings. Retained so packs authored before
    /// issue #109 keep loading verbatim. On load, the flattened
    /// <see cref="Tasks"/> list drives every downstream consumer.
    /// </summary>
    public List<string> Prompts { get; init; } = [];
}

/// <summary>
/// One curated starting task in a category. Distinguishes the display
/// name shown to the user from the prompt string that is actually
/// submitted to the chat backend so a short, scannable label can invoke
/// a fully-formed question.
/// </summary>
public sealed class PackStartingTask
{
    /// <summary>Short display name shown on the suggestion button.</summary>
    public string Name { get; init; } = "";

    /// <summary>Verbatim prompt text submitted to the chat backend.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>
    /// Optional explicit ordering. Tasks with a lower <see cref="Order"/>
    /// value render first within their category; ties break on source-array
    /// position.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Optional declarative capability the task showcases. Purely
    /// descriptive metadata — never changes execution behavior. Used by
    /// the frontend to expose <c>data-capability-*</c> attributes so a
    /// scenario can pin which chart type or plan path a task exercises.
    /// </summary>
    public PackStartingTaskCapability? Capability { get; init; }
}

/// <summary>
/// Declarative capability metadata attached to a starting task.
/// </summary>
public sealed class PackStartingTaskCapability
{
    /// <summary>
    /// Capability family the task showcases. Accepted values (case-
    /// insensitive): <c>prose</c>, <c>chart</c>, <c>plan</c>. The loader
    /// rejects any other value so a typo cannot silently ship.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// Chart type the task expects to render. Required when
    /// <see cref="Kind"/> is <c>chart</c>. Free-form so pack authors can
    /// name any chart the render pipeline supports.
    /// </summary>
    public string? ChartType { get; init; }

    /// <summary>
    /// Plan path identifier the task expects to exercise. Required when
    /// <see cref="Kind"/> is <c>plan</c>. Free-form so pack authors can
    /// name any orchestration path the platform supports.
    /// </summary>
    public string? PlanPath { get; init; }
}
