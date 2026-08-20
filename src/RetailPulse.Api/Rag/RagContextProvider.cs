using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Middleware that injects relevant RAG context into agent prompts.
/// Searches the knowledge base for relevant chunks and appends them
/// as reference context in the conversation history.
///
/// Per-agent knowledge binding (issue #105): when a
/// <see cref="KnowledgeSourceRegistry"/> is supplied, the provider consults the
/// resolved binding for the routed agent before touching the knowledge
/// backend. A disabled binding short-circuits with zero provider latency and
/// zero token cost. A scoped binding pushes the source filter down to the
/// provider so top-K/scoring selects from in-scope hits only.
///
/// Retrieved chunks are treated as untrusted and always flow through the
/// existing Content Safety indirect-injection path. The concatenated grounding
/// block is bounded by <see cref="ToolResultBudgetOptions.MaxResultChars"/>
/// (ADR-006) so retrieval cannot amplify model context beyond the shared tool
/// budget.
/// </summary>
public class RagContextProvider
{
    private readonly IKnowledgeBase _knowledgeBase;
    private readonly ILogger<RagContextProvider> _logger;
    private readonly IContentSafetyEvaluator _contentSafety;
    private readonly ISuspiciousRequestLog? _suspiciousLog;
    private readonly GuardrailsConfig _guardrailsConfig;
    private readonly KnowledgeSourceRegistry? _sourceRegistry;
    private readonly ToolResultBudgetOptions _budget;
    private const int _topK = 3;
    private const double _minRelevanceScore = 0.3;

    public RagContextProvider(
        IKnowledgeBase knowledgeBase,
        ILogger<RagContextProvider> logger,
        IContentSafetyEvaluator? contentSafety = null,
        ISuspiciousRequestLog? suspiciousLog = null,
        GuardrailsConfig? guardrailsConfig = null,
        KnowledgeSourceRegistry? sourceRegistry = null,
        IOptions<ToolResultBudgetOptions>? budgetOptions = null)
    {
        _knowledgeBase = knowledgeBase;
        _logger = logger;
        _contentSafety = contentSafety ?? NoOpContentSafetyEvaluator.Instance;
        _suspiciousLog = suspiciousLog;
        _guardrailsConfig = guardrailsConfig ?? new GuardrailsConfig();
        _sourceRegistry = sourceRegistry;
        _budget = budgetOptions?.Value ?? new ToolResultBudgetOptions();
    }

    /// <summary>
    /// Search the knowledge base for context relevant to the user's message.
    /// Returns a formatted string to inject into the system prompt, or null if nothing relevant found.
    /// </summary>
    public Task<string?> GetContextAsync(string userMessage, CancellationToken ct = default) =>
        GetContextAsync(userMessage, userId: "anonymous", ct);

    /// <summary>
    /// Search the knowledge base for context relevant to the user's message,
    /// optionally scoping Content Safety audit rows to the calling principal.
    /// </summary>
    public async Task<string?> GetContextAsync(string userMessage, string userId, CancellationToken ct = default)
    {
        RagRetrievalOutcome outcome = await GetContextForAgentAsync(
            userMessage, userId, agentKey: string.Empty, ct).ConfigureAwait(false);
        return outcome.Context;
    }

    /// <summary>
    /// Agent-scoped retrieval (issue #105). Consults the
    /// <see cref="KnowledgeSourceRegistry"/> for the routed agent's binding
    /// and short-circuits when knowledge is disabled. Emits an activity with
    /// <c>span.type=retrieval</c> and metadata callers can capture into the
    /// request's <c>TraceSpan</c> stream.
    /// </summary>
    public async Task<RagRetrievalOutcome> GetContextForAgentAsync(
        string userMessage,
        string userId,
        string agentKey,
        CancellationToken ct = default)
    {
        KnowledgeBinding binding = _sourceRegistry?.GetBinding(agentKey) ?? KnowledgeSourceRegistry.Default;
        if (!binding.Enabled)
        {
            // Hard short-circuit BEFORE any provider access or activity
            // creation — the disabled path must add zero retrieval latency
            // and zero token cost. Callers still see a well-defined outcome
            // so telemetry can record the skip if desired.
            _logger.LogDebug("RAG retrieval skipped for agent '{AgentKey}' — knowledge disabled", agentKey);
            return RagRetrievalOutcome.Skipped(agentKey);
        }

        long startTicks = Stopwatch.GetTimestamp();
        using Activity? activity = AgentTelemetry.StartRetrieval(agentKey);
        activity?.SetTag("span.type", "retrieval");
        activity?.SetTag("retrieval.enabled", true);
        activity?.SetTag("retrieval.scoped", binding.IsScoped);
        if (binding.Sources.Count > 0)
        {
            activity?.SetTag("retrieval.source", string.Join(",", binding.Sources));
        }

        try
        {
            IReadOnlyList<SearchResult> results = binding.Sources.Count > 0
                ? await _knowledgeBase.SearchAsync(userMessage, _topK, binding.Sources, ct).ConfigureAwait(false)
                : await _knowledgeBase.SearchAsync(userMessage, _topK, ct).ConfigureAwait(false);

            var relevant = results.Where(r => r.Score >= _minRelevanceScore).ToList();
            if (relevant.Count == 0)
            {
                double emptyDurationMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                activity?.SetTag("retrieval.chunk_count", 0);
                activity?.SetTag("retrieval.duration_ms", emptyDurationMs);
                return new RagRetrievalOutcome(
                    Context: null,
                    Enabled: true,
                    Scoped: binding.IsScoped,
                    Sources: binding.Sources,
                    AgentKey: agentKey,
                    ChunkCount: 0,
                    DurationMs: emptyDurationMs,
                    BudgetTrimmedChunks: 0);
            }

            relevant = await FilterByContentSafetyAsync(relevant, userId, ct).ConfigureAwait(false);
            if (relevant.Count == 0)
            {
                _logger.LogWarning(
                    "All retrieved knowledge chunks were dropped by Content Safety for user {UserId}", userId);
                double safetyDurationMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
                activity?.SetTag("retrieval.chunk_count", 0);
                activity?.SetTag("retrieval.duration_ms", safetyDurationMs);
                return new RagRetrievalOutcome(
                    Context: null,
                    Enabled: true,
                    Scoped: binding.IsScoped,
                    Sources: binding.Sources,
                    AgentKey: agentKey,
                    ChunkCount: 0,
                    DurationMs: safetyDurationMs,
                    BudgetTrimmedChunks: 0);
            }

            (string context, int keptChunks, int trimmedChunks) = FormatWithinBudget(relevant);
            double durationMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

            activity?.SetTag("retrieval.chunk_count", keptChunks);
            activity?.SetTag("retrieval.duration_ms", durationMs);
            if (trimmedChunks > 0)
            {
                activity?.SetTag("retrieval.budget_trimmed", trimmedChunks);
            }

            _logger.LogDebug(
                "RAG context injected for agent '{AgentKey}': {ResultCount} chunks kept, {Trimmed} dropped by budget, query '{Query}'",
                agentKey, keptChunks, trimmedChunks,
                userMessage[..Math.Min(50, userMessage.Length)]);

            return new RagRetrievalOutcome(
                Context: context,
                Enabled: true,
                Scoped: binding.IsScoped,
                Sources: binding.Sources,
                AgentKey: agentKey,
                ChunkCount: keptChunks,
                DurationMs: durationMs,
                BudgetTrimmedChunks: trimmedChunks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG context retrieval failed — proceeding without context");
            double failedDurationMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            activity?.SetTag("retrieval.chunk_count", 0);
            activity?.SetTag("retrieval.duration_ms", failedDurationMs);
            activity?.SetTag("retrieval.error", ex.GetType().Name);
            return new RagRetrievalOutcome(
                Context: null,
                Enabled: true,
                Scoped: binding.IsScoped,
                Sources: binding.Sources,
                AgentKey: agentKey,
                ChunkCount: 0,
                DurationMs: failedDurationMs,
                BudgetTrimmedChunks: 0);
        }
    }

    /// <summary>
    /// Serializes <paramref name="relevant"/> into the grounding block, dropping
    /// tail chunks that would exceed <see cref="ToolResultBudgetOptions.MaxResultChars"/>
    /// so retrieval respects the same context ceiling as tool results
    /// (ADR-006). At least one chunk is always kept so a barely-oversized
    /// single hit still grounds the response.
    /// </summary>
    private (string Context, int Kept, int Trimmed) FormatWithinBudget(List<SearchResult> relevant)
    {
        int budget = Math.Max(_budget.MaxResultChars, 512);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- Reference Context (from knowledge base) ---");
        sb.AppendLine("Use the following grounded information to inform your response. Cite sources when relevant.");
        sb.AppendLine();

        int kept = 0;
        int trimmed = 0;

        foreach (SearchResult result in relevant)
        {
            string chunkHeader = string.Create(CultureInfo.InvariantCulture, $"[Source: {result.Title}, chunk {result.ChunkIndex}] (relevance: {result.Score.ToString("F2", CultureInfo.InvariantCulture)})");
            int projected = sb.Length + chunkHeader.Length + result.Chunk.Length + 4; // headers + blank line separators
            if (kept > 0 && projected > budget)
            {
                trimmed = relevant.Count - kept;
                break;
            }

            sb.AppendLine(chunkHeader);
            sb.AppendLine(result.Chunk);
            sb.AppendLine();
            kept++;
        }

        sb.AppendLine("--- End Reference Context ---");
        return (sb.ToString(), kept, trimmed);
    }

    private async Task<List<SearchResult>> FilterByContentSafetyAsync(
        List<SearchResult> chunks,
        string userId,
        CancellationToken ct)
    {
        ContentSafetyConfig cs = _guardrailsConfig.ContentSafety;
        if (!cs.Enabled || !cs.CheckRetrievedKnowledge || chunks.Count == 0)
        {
            return chunks;
        }

        var survivors = new List<SearchResult>(chunks.Count);
        foreach (SearchResult chunk in chunks)
        {
            ContentSafetyResult evaluation = await _contentSafety.EvaluateAsync(
                chunk.Chunk,
                ContentSafetyStage.RetrievedKnowledge,
                new ContentSafetyEvaluationContext(
                    UserId: userId,
                    SourceId: $"{chunk.Title}#{chunk.ChunkIndex}",
                    CheckPromptShield: cs.PromptShieldsEnabled),
                ct).ConfigureAwait(false);

            switch (evaluation.Decision)
            {
                case ContentSafetyDecision.Blocked:
                    {
                        // On the RAG stage, indirect-injection is the primary
                        // signal — Prompt Shields runs the chunk as a
                        // document, so we prefer that classification over a
                        // user-prompt jailbreak flag when both are set.
                        string detectionType = ContentSafetyDetectionTypes.ForResultWithShield(
                            evaluation, preferIndirect: true);
                        (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                        await LogAsync(new SuspiciousRequest(
                            Guid.NewGuid().ToString("N"),
                            DateTime.UtcNow,
                            $"Retrieved-knowledge chunk '{chunk.Title}#{chunk.ChunkIndex}' dropped by Content Safety",
                            detectionType,
                            userId,
                            ContentSafetyActions.Dropped,
                            Category: category,
                            Severity: severity,
                            Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Content Safety dropped RAG chunk '{Title}#{Chunk}' (type={Detection})",
                            chunk.Title, chunk.ChunkIndex, detectionType);
                        continue;
                    }
                case ContentSafetyDecision.ServiceUnavailable:
                    {
                        string action = cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed
                            ? ContentSafetyActions.FailClosedBlocked
                            : ContentSafetyActions.FailOpenPassed;
                        await LogAsync(new SuspiciousRequest(
                            Guid.NewGuid().ToString("N"),
                            DateTime.UtcNow,
                            $"Content Safety unavailable while checking RAG chunk '{chunk.Title}#{chunk.ChunkIndex}'",
                            ContentSafetyDetectionTypes.Unavailable,
                            userId,
                            action,
                            Category: null,
                            Severity: null,
                            Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);
                        if (cs.OnUnavailable == ContentSafetyFailPolicy.FailClosed)
                        {
                            _logger.LogWarning(
                                "Content Safety unavailable — fail-closed policy dropping RAG chunk '{Title}#{Chunk}'",
                                chunk.Title, chunk.ChunkIndex);
                            continue;
                        }
                        survivors.Add(chunk);
                        break;
                    }
                case ContentSafetyDecision.Flagged:
                    {
                        (string? category, int? severity) = PickCategoryAndSeverity(evaluation);
                        await LogAsync(new SuspiciousRequest(
                            Guid.NewGuid().ToString("N"),
                            DateTime.UtcNow,
                            $"Retrieved-knowledge chunk '{chunk.Title}#{chunk.ChunkIndex}' flagged by Content Safety",
                            ContentSafetyDetectionTypes.ForResultWithShield(evaluation, preferIndirect: true),
                            userId,
                            ContentSafetyActions.Flagged,
                            Category: category,
                            Severity: severity,
                            Decision: evaluation.Decision.ToString()), ct).ConfigureAwait(false);
                        survivors.Add(chunk);
                        break;
                    }
                case ContentSafetyDecision.Passed:
                default:
                    survivors.Add(chunk);
                    break;
            }
        }
        return survivors;
    }

    private Task LogAsync(SuspiciousRequest request, CancellationToken ct) =>
        _suspiciousLog?.LogAsync(request, ct) ?? Task.CompletedTask;

    private static (string? Category, int? Severity) PickCategoryAndSeverity(ContentSafetyResult evaluation)
    {
        if (evaluation.Categories.Count == 0) return (null, null);
        ContentSafetyCategoryHit top = evaluation.Categories[0];
        for (int i = 1; i < evaluation.Categories.Count; i++)
        {
            if (evaluation.Categories[i].Severity > top.Severity)
                top = evaluation.Categories[i];
        }
        return (top.Category, top.Severity);
    }
}
