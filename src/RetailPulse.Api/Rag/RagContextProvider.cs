using System.Globalization;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Middleware that injects relevant RAG context into agent prompts.
/// Searches the knowledge base for relevant chunks and appends them
/// as reference context in the conversation history.
/// </summary>
public class RagContextProvider
{
    private readonly IKnowledgeBase _knowledgeBase;
    private readonly ILogger<RagContextProvider> _logger;
    private readonly IContentSafetyEvaluator _contentSafety;
    private readonly ISuspiciousRequestLog? _suspiciousLog;
    private readonly GuardrailsConfig _guardrailsConfig;
    private const int _topK = 3;
    private const double _minRelevanceScore = 0.3;

    public RagContextProvider(
        IKnowledgeBase knowledgeBase,
        ILogger<RagContextProvider> logger,
        IContentSafetyEvaluator? contentSafety = null,
        ISuspiciousRequestLog? suspiciousLog = null,
        GuardrailsConfig? guardrailsConfig = null)
    {
        _knowledgeBase = knowledgeBase;
        _logger = logger;
        _contentSafety = contentSafety ?? NoOpContentSafetyEvaluator.Instance;
        _suspiciousLog = suspiciousLog;
        _guardrailsConfig = guardrailsConfig ?? new GuardrailsConfig();
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
        try
        {
            IReadOnlyList<SearchResult> results = await _knowledgeBase.SearchAsync(userMessage, _topK, ct);

            var relevant = results.Where(r => r.Score >= _minRelevanceScore).ToList();
            if (relevant.Count == 0)
                return null;

            relevant = await FilterByContentSafetyAsync(relevant, userId, ct).ConfigureAwait(false);
            if (relevant.Count == 0)
            {
                _logger.LogWarning(
                    "All retrieved knowledge chunks were dropped by Content Safety for user {UserId}", userId);
                return null;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("--- Reference Context (from knowledge base) ---");
            sb.AppendLine("Use the following grounded information to inform your response. Cite sources when relevant.");
            sb.AppendLine();

            foreach (SearchResult? result in relevant)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"[Source: {result.Title}, chunk {result.ChunkIndex}] (relevance: {result.Score.ToString("F2", CultureInfo.InvariantCulture)})");
                sb.AppendLine(result.Chunk);
                sb.AppendLine();
            }

            sb.AppendLine("--- End Reference Context ---");

            _logger.LogDebug("RAG context injected: {ResultCount} chunks for query '{Query}'",
                relevant.Count, userMessage[..Math.Min(50, userMessage.Length)]);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG context retrieval failed — proceeding without context");
            return null;
        }
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
                        string detectionType = evaluation.PromptShieldIndirectInjectionDetected
                            ? ContentSafetyDetectionTypes.IndirectInjection
                            : evaluation.PromptShieldJailbreakDetected
                                ? ContentSafetyDetectionTypes.PromptShield
                                : evaluation.PrimaryCategory ?? ContentSafetyDetectionTypes.IndirectInjection;
                        await LogAsync(new SuspiciousRequest(
                            Guid.NewGuid().ToString("N"),
                            DateTime.UtcNow,
                            $"Retrieved-knowledge chunk '{chunk.Title}#{chunk.ChunkIndex}' dropped by Content Safety",
                            detectionType,
                            userId,
                            ContentSafetyActions.Dropped), ct).ConfigureAwait(false);
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
                            action), ct).ConfigureAwait(false);
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
                    await LogAsync(new SuspiciousRequest(
                        Guid.NewGuid().ToString("N"),
                        DateTime.UtcNow,
                        $"Retrieved-knowledge chunk '{chunk.Title}#{chunk.ChunkIndex}' flagged by Content Safety",
                        evaluation.PrimaryCategory ?? ContentSafetyDetectionTypes.IndirectInjection,
                        userId,
                        ContentSafetyActions.Flagged), ct).ConfigureAwait(false);
                    survivors.Add(chunk);
                    break;
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
}
