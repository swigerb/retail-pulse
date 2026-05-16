using System.Globalization;
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
    private const int _topK = 3;
    private const double _minRelevanceScore = 0.3;

    public RagContextProvider(IKnowledgeBase knowledgeBase, ILogger<RagContextProvider> logger)
    {
        _knowledgeBase = knowledgeBase;
        _logger = logger;
    }

    /// <summary>
    /// Search the knowledge base for context relevant to the user's message.
    /// Returns a formatted string to inject into the system prompt, or null if nothing relevant found.
    /// </summary>
    public async Task<string?> GetContextAsync(string userMessage, CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<SearchResult> results = await _knowledgeBase.SearchAsync(userMessage, _topK, ct);

            var relevant = results.Where(r => r.Score >= _minRelevanceScore).ToList();
            if (relevant.Count == 0)
                return null;

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
}
