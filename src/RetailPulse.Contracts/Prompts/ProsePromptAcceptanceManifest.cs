using RetailPulse.Contracts.Routing;

namespace RetailPulse.Contracts.Prompts;

/// <summary>
/// One acceptance contract for a curated PROSE (non-chart) prompt: the verbatim prompt
/// text, its owning category, the specialist <see cref="AgentIntent"/> the router MUST
/// classify it into, and the specialist agent key that owns the fulfillment path.
///
/// This is the prose analogue of <see cref="Charts.ChartAcceptanceCase"/>. Together the
/// two manifests cover the full curated prompt library in
/// <c>src/RetailPulse.Web/src/constants/prompts.ts</c> — chart-oriented prompts on the
/// chart manifest, prose-oriented prompts on this manifest. Contract tests fail CI when
/// either manifest drifts from the frontend prompt source.
/// </summary>
/// <param name="Prompt">The verbatim curated prompt text (single source of truth).</param>
/// <param name="CategoryId">The <c>PROMPT_CATEGORIES</c> id the prompt belongs to.</param>
/// <param name="ExpectedIntent">
/// The <see cref="AgentIntent"/> the router MUST classify this prompt into. Must never
/// be <see cref="AgentIntent.PortfolioHealth"/> — a prose curated prompt that lands on
/// the multi-agent council produces a slow, prose-only response with no specialist
/// tool-loop, which is the exact "empty/underpowered response" symptom this manifest
/// exists to prevent.
/// </param>
/// <param name="ExpectedAgentKey">
/// The DI key of the specialist that must own this request (e.g. <c>demand-forecasting</c>,
/// <c>general</c>, <c>field-sentiment</c>). Must map to a specialist declared in
/// <c>prompts.yaml</c> with a non-empty tool list, so the routed prompt actually
/// invokes at least one tool (guarding the "returns a routed, tool-backed response"
/// invariant the live browser sweep in issue #63 relies on).
/// </param>
/// <param name="Rationale">Short human note on why this routing is expected.</param>
public sealed record ProsePromptAcceptanceCase(
    string Prompt,
    string CategoryId,
    string ExpectedIntent,
    string ExpectedAgentKey,
    string Rationale);

/// <summary>
/// Canonical acceptance manifest for every curated PROSE prompt across the six
/// tenant/domain-neutral categories exposed by the "Prompt ideas" popover in
/// <c>src/RetailPulse.Web/src/constants/prompts.ts</c>. The Charts category
/// (plus the previously-validated QSR two-brand comparison) is covered by
/// <see cref="Charts.ChartAcceptanceManifest"/>; every remaining curated prompt
/// is covered here.
///
/// Two contract tests enforce cross-language sync:
/// <list type="bullet">
///   <item><c>ProsePromptAcceptanceManifestContractTests</c> — this manifest must
///     mirror every non-chart curated prompt in <c>prompts.ts</c> exactly (no
///     drift, no orphans, in source order).</item>
///   <item><c>ProsePromptRoutingAcceptanceTests</c> — every case must route through
///     <c>RetailOpsRouter</c> to its expected specialist, never to the council,
///     and must never be classified by <c>ChartRequestDetector</c> as an explicit
///     chart request (guards against "prose leaks chart JSON" regressions).</item>
/// </list>
/// </summary>
public static class ProsePromptAcceptanceManifest
{
    /// <summary>
    /// The prose curated prompts in the order they appear in <c>prompts.ts</c>. Every
    /// non-chart entry from every category is enumerated exactly once. The Charts
    /// category and the QSR two-brand comparison prompt (both covered by
    /// <see cref="Charts.ChartAcceptanceManifest"/>) are intentionally excluded here.
    /// </summary>
    public static readonly IReadOnlyList<ProsePromptAcceptanceCase> Cases =
    [
        // ── General Retail ────────────────────────────────────────────────
        new(
            Prompt: "Compare depletion trends across all regions for this quarter",
            CategoryId: "general",
            ExpectedIntent: AgentIntent.DemandForecasting,
            ExpectedAgentKey: "demand-forecasting",
            Rationale: "cross-region depletion comparison — router fast-path bypasses the LLM"),

        new(
            Prompt: "Which brands are growing fastest year-over-year across the portfolio?",
            CategoryId: "general",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "portfolio-ranking growth question — General agent owns the aggregate tool"),

        new(
            Prompt: "Show me field sentiment for our top 3 brands in the Southeast",
            CategoryId: "general",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),

        // ── Grocery ───────────────────────────────────────────────────────
        new(
            Prompt: "How are FreshMart depletions trending in the Northeast this quarter?",
            CategoryId: "grocery",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "simple single-brand depletion trend — router fast-path → General for one MCP call"),

        new(
            Prompt: "Compare Harvest Table vs FreshMart sell-through rates by region",
            CategoryId: "grocery",
            ExpectedIntent: AgentIntent.DemandForecasting,
            ExpectedAgentKey: "demand-forecasting",
            Rationale: "'sell-through' keyword fast-paths to the demand-forecasting specialist"),

        new(
            Prompt: "What is the field sentiment for Harvest Table Meal Kits in the Midwest?",
            CategoryId: "grocery",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),

        // ── Quick-Serve Restaurants ───────────────────────────────────────
        new(
            Prompt: "How is Apex Grill performing in the Southwest this quarter?",
            CategoryId: "qsr",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "single-brand performance lookup — router fast-path → General"),

        // "Compare Coastline Tacos vs Apex Grill depletions across all regions" is a
        // chart-comparison prompt covered by ChartAcceptanceManifest; excluded here.

        new(
            Prompt: "What is the field sentiment for Coastline Tacos in the West Coast?",
            CategoryId: "qsr",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),

        // ── Home Improvement ──────────────────────────────────────────────
        new(
            Prompt: "Show me Pinnacle Hardware depletion stats in the Midwest for Q1",
            CategoryId: "home-improvement",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "single-brand depletion stats lookup — router fast-path → General"),

        new(
            Prompt: "How is Summit Outdoor performing in the Southeast vs West Coast?",
            CategoryId: "home-improvement",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "single-brand performance lookup — router fast-path → General"),

        new(
            Prompt: "What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?",
            CategoryId: "home-improvement",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),

        // ── Office Supply ─────────────────────────────────────────────────
        new(
            Prompt: "How are ClearDesk depletions trending in the Northeast this quarter?",
            CategoryId: "office-supply",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "single-brand depletion trend — router fast-path → General"),

        new(
            Prompt: "Compare ClearDesk Technology vs Paper Products sell-through by region",
            CategoryId: "office-supply",
            ExpectedIntent: AgentIntent.DemandForecasting,
            ExpectedAgentKey: "demand-forecasting",
            Rationale: "'sell-through' keyword fast-paths to the demand-forecasting specialist"),

        new(
            Prompt: "What is the field sentiment for ClearDesk in the Southeast?",
            CategoryId: "office-supply",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),

        // ── Furniture ─────────────────────────────────────────────────────
        new(
            Prompt: "Show me Urban Living depletion trends across all regions this quarter",
            CategoryId: "furniture",
            ExpectedIntent: AgentIntent.General,
            ExpectedAgentKey: "general",
            Rationale: "single-brand depletion trend lookup — router fast-path → General"),

        new(
            Prompt: "Compare Foundry Home vs Urban Living performance in the West Coast",
            CategoryId: "furniture",
            ExpectedIntent: AgentIntent.DemandForecasting,
            ExpectedAgentKey: "demand-forecasting",
            Rationale: "cross-brand performance comparison — LLM classification lands on demand-forecasting"),

        new(
            Prompt: "What is the field sentiment for Urban Living in the Pacific Northwest?",
            CategoryId: "furniture",
            ExpectedIntent: AgentIntent.SentimentField,
            ExpectedAgentKey: "field-sentiment",
            Rationale: "'sentiment' keyword fast-paths to the field-sentiment specialist"),
    ];

    /// <summary>The verbatim curated prose prompts, in manifest order.</summary>
    public static IReadOnlyList<string> Prompts => [.. Cases.Select(c => c.Prompt)];

    /// <summary>
    /// Category ids from <c>prompts.ts</c> that this manifest fully covers (every
    /// prose prompt in the category has a case). Excludes the <c>charts</c> category,
    /// which is fully owned by <see cref="Charts.ChartAcceptanceManifest"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> CoveredCategoryIds =
    [
        "general", "grocery", "qsr", "home-improvement", "office-supply", "furniture",
    ];
}
