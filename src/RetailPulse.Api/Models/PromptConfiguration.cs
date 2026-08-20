namespace RetailPulse.Api.Models;

public class PromptConfiguration
{
    public Dictionary<string, AgentDefinition> Agents { get; set; } = [];
}

public class AgentDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
    public string SystemPrompt { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public List<string> Tools { get; set; } = [];

    /// <summary>
    /// Routing key used by the router to dispatch to this agent (e.g., "demand-forecasting").
    /// When absent, the loader defaults it to the YAML section name during composition.
    /// Lowercase kebab-case by convention.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name used in telemetry and UI. Falls back to <see cref="Name"/>
    /// when empty.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Intents this specialist handles. The router derives its intent set from the
    /// union of every configured specialist's <see cref="Intents"/> list — no compiled
    /// enum extension is required to add a new specialist.
    /// </summary>
    public List<string> Intents { get; set; } = [];

    /// <summary>
    /// Case-insensitive substrings that force a keyword fast-path match to this agent's
    /// primary intent. Empty by default; add strong, unambiguous phrases only — short or
    /// generic keywords should be left to the LLM classifier.
    /// </summary>
    public List<string> KeywordFastPaths { get; set; } = [];

    /// <summary>
    /// Customized fallback reply used when the LLM returns an empty response. Falls back
    /// to a domain-neutral default when unspecified.
    /// </summary>
    public string FallbackReply { get; set; } = string.Empty;

    /// <summary>
    /// True when this specialist should be included in the Portfolio Health Council fan-out.
    /// Replaces the previously hardcoded council roster in <c>ConsensusOrchestrator</c>.
    /// </summary>
    public bool CouncilParticipant { get; set; }

    /// <summary>
    /// Optional brand-scorecard dimension supplied by this specialist. When both
    /// <see cref="ScorecardDimension"/> and <see cref="ScorecardWeight"/> are set, the
    /// <c>ScorecardOrchestrator</c> includes this agent as a scoring dimension.
    /// </summary>
    public string ScorecardDimension { get; set; } = string.Empty;

    /// <summary>
    /// Scorecard dimension weight (0–1). Combined with <see cref="ScorecardDimension"/>.
    /// </summary>
    public double ScorecardWeight { get; set; }

    /// <summary>
    /// Role of this definition in the composition graph. Defaults to <c>"specialist"</c> —
    /// the loader will construct a <c>ConfiguredSpecialistAgent</c>. Use <c>"bespoke"</c>
    /// for agents that ship a hand-written class (Memory Management, Competitive Intel),
    /// or <c>"orchestration"</c> for router/synthesizer/vote-format prompts that are not
    /// specialists at all.
    /// </summary>
    public string Role { get; set; } = "specialist";

    /// <summary>
    /// True when the router should call <see cref="Agents.IPrefetchableAgent"/>
    /// on this specialist. Only Demand Forecasting uses prefetch today.
    /// </summary>
    public bool Prefetchable { get; set; }

    /// <summary>
    /// Per-agent RAG knowledge binding toggle (issue #105). Default <c>true</c>
    /// preserves the pre-#105 behavior of injecting retrieved context for
    /// every routed specialist. Set to <c>false</c> in prompts.yaml
    /// (<c>use_knowledge_base: false</c>) for agents that must never issue a
    /// retrieval — the pipeline short-circuits before touching the knowledge
    /// provider, so no latency and no token cost accrues.
    /// </summary>
    public bool UseKnowledgeBase { get; set; } = true;

    /// <summary>
    /// Optional logical knowledge source name (issue #105) — resolved at
    /// startup against <c>Knowledge:Sources:Named</c>. When empty the agent
    /// uses the entire corpus (unscoped retrieval). An unknown value aborts
    /// startup with an actionable error. YAML field:
    /// <c>knowledge_base_name</c>.
    /// </summary>
    public string KnowledgeBaseName { get; set; } = string.Empty;

    /// <summary>Convenience — resolves the effective display name (falls back to <see cref="Name"/>).</summary>
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>Convenience — resolves the effective fallback reply.</summary>
    public string EffectiveFallbackReply => string.IsNullOrWhiteSpace(FallbackReply)
        ? "I wasn't able to generate a response."
        : FallbackReply;

    /// <summary>
    /// Returns a shallow member-wise copy with new list instances for the mutable
    /// collections. Used by the specialist shims' <c>EnsureDefaults</c> so populating
    /// per-agent defaults never mutates a shared <see cref="AgentDefinition"/> instance
    /// passed to multiple specialists (a real hazard in tests that reuse a stub def).
    /// </summary>
    public AgentDefinition Clone() => new()
    {
        Name = Name,
        Model = Model,
        SystemPrompt = SystemPrompt,
        Temperature = Temperature,
        Tools = [.. Tools],
        Key = Key,
        DisplayName = DisplayName,
        Intents = [.. Intents],
        KeywordFastPaths = [.. KeywordFastPaths],
        FallbackReply = FallbackReply,
        CouncilParticipant = CouncilParticipant,
        ScorecardDimension = ScorecardDimension,
        ScorecardWeight = ScorecardWeight,
        Role = Role,
        Prefetchable = Prefetchable,
        UseKnowledgeBase = UseKnowledgeBase,
        KnowledgeBaseName = KnowledgeBaseName,
    };
}
