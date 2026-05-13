using System.Collections.Concurrent;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// In-memory knowledge base using BM25 scoring for document retrieval.
/// Thread-safe via ConcurrentDictionary. No external dependencies required.
/// </summary>
public sealed class InMemoryKnowledgeBase : IKnowledgeBase
{
    // BM25 parameters (standard values)
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double MinScoreThreshold = 0.3;

    private readonly ConcurrentDictionary<string, IndexedDocument> _documents = new();
    private readonly ConcurrentDictionary<string, IndexedChunk> _chunks = new();
    private readonly ILogger<InMemoryKnowledgeBase> _logger;

    // Global corpus stats for BM25
    private double _avgDocLength;
    private int _totalChunks;
    private readonly object _statsLock = new();

    public InMemoryKnowledgeBase(ILogger<InMemoryKnowledgeBase> logger)
    {
        _logger = logger;
    }

    public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default)
    {
        var documentId = Guid.NewGuid().ToString("N")[..12];
        var chunks = DocumentChunker.Chunk(content);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("Document '{Title}' produced no chunks — skipping", title);
            return Task.FromResult(documentId);
        }

        var doc = new IndexedDocument(documentId, title, source, DateTime.UtcNow, chunks.Count);
        _documents[documentId] = doc;

        foreach (var chunk in chunks)
        {
            var chunkId = $"{documentId}:{chunk.Index}";
            var tokens = Tokenize(chunk.Text);
            var termFrequencies = BuildTermFrequencies(tokens);

            _chunks[chunkId] = new IndexedChunk(
                chunkId, documentId, title, source,
                chunk.Text, chunk.Index, chunk.SectionHeader,
                tokens.Length, termFrequencies);
        }

        // Update corpus stats
        lock (_statsLock)
        {
            _totalChunks = _chunks.Count;
            _avgDocLength = _chunks.Values.Average(c => c.TokenCount);
        }

        _logger.LogInformation("Ingested document '{Title}' ({Source}): {ChunkCount} chunks, id={Id}",
            title, source, chunks.Count, documentId);

        return Task.FromResult(documentId);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (_chunks.IsEmpty)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var queryTerms = Tokenize(query);
        if (queryTerms.Length == 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // Compute IDF for each query term
        var idfScores = new Dictionary<string, double>();
        foreach (var term in queryTerms.Distinct())
        {
            var docsContaining = _chunks.Values.Count(c => c.TermFrequencies.ContainsKey(term));
            idfScores[term] = Math.Log((_totalChunks - docsContaining + 0.5) / (docsContaining + 0.5) + 1.0);
        }

        // Score each chunk via BM25
        var scored = new List<(IndexedChunk Chunk, double Score)>();
        foreach (var chunk in _chunks.Values)
        {
            var score = 0.0;
            foreach (var term in queryTerms)
            {
                if (!chunk.TermFrequencies.TryGetValue(term, out var tf))
                    continue;

                var idf = idfScores.GetValueOrDefault(term, 0);
                var numerator = tf * (K1 + 1);
                var denominator = tf + K1 * (1 - B + B * chunk.TokenCount / _avgDocLength);
                score += idf * (numerator / denominator);
            }

            if (score > MinScoreThreshold)
                scored.Add((chunk, score));
        }

        // Normalize scores to 0-1 range
        var maxScore = scored.Count > 0 ? scored.Max(s => s.Score) : 1.0;
        var results = scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select(s => new SearchResult(
                s.Chunk.DocumentId,
                s.Chunk.Title,
                s.Chunk.Text,
                Math.Round(s.Score / maxScore, 4),
                s.Chunk.Source,
                s.Chunk.ChunkIndex))
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default)
    {
        var docs = _documents.Values
            .OrderByDescending(d => d.IngestedAt)
            .Select(d => new DocumentInfo(d.Id, d.Title, d.Source, d.IngestedAt, d.ChunkCount))
            .ToList();

        return Task.FromResult<IReadOnlyList<DocumentInfo>>(docs);
    }

    public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
    {
        _documents.TryRemove(documentId, out _);

        var chunkKeys = _chunks.Keys.Where(k => k.StartsWith(documentId + ":")).ToList();
        foreach (var key in chunkKeys)
            _chunks.TryRemove(key, out _);

        // Update corpus stats
        lock (_statsLock)
        {
            _totalChunks = _chunks.Count;
            _avgDocLength = _chunks.Count > 0 ? _chunks.Values.Average(c => c.TokenCount) : 0;
        }

        _logger.LogInformation("Deleted document {DocumentId} and {ChunkCount} chunks", documentId, chunkKeys.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Check if a document with the given title already exists (for idempotent seeding).
    /// </summary>
    public bool HasDocument(string title) =>
        _documents.Values.Any(d => string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase));

    public int DocumentCount => _documents.Count;
    public int ChunkCount => _chunks.Count;

    private static string[] Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '-', '—'))
            .Where(t => t.Length > 1)
            .ToArray();

    private static Dictionary<string, int> BuildTermFrequencies(string[] tokens)
    {
        var tf = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            tf.TryGetValue(token, out var count);
            tf[token] = count + 1;
        }
        return tf;
    }

    // Internal records
    private record IndexedDocument(string Id, string Title, string Source, DateTime IngestedAt, int ChunkCount);

    private record IndexedChunk(
        string Id, string DocumentId, string Title, string Source,
        string Text, int ChunkIndex, string? SectionHeader,
        int TokenCount, Dictionary<string, int> TermFrequencies);
}
