using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// In-memory knowledge base using BM25 scoring for document retrieval.
/// Thread-safe via ConcurrentDictionary. Enforces configurable quotas on
/// document count, chunk count, and individual document size.
/// </summary>
public sealed class InMemoryKnowledgeBase : IKnowledgeBase
{
    /// <summary>Stable name reported in <see cref="GetCapabilities"/>.</summary>
    public const string ProviderName = "InMemory";

    // BM25 parameters (standard values)
    private const double _k1 = 1.2;
    private const double _b = 0.75;
    private const double _minScoreThreshold = 0.3;

    private readonly ConcurrentDictionary<string, IndexedDocument> _documents = new();
    private readonly ConcurrentDictionary<string, IndexedChunk> _chunks = new();
    private readonly ILogger<InMemoryKnowledgeBase> _logger;
    private readonly KnowledgeOptions _options;

    // Global corpus stats for BM25
    private double _avgDocLength;
    private int _totalChunks;
    private readonly Lock _statsLock = new();

    public InMemoryKnowledgeBase(ILogger<InMemoryKnowledgeBase> logger, IOptions<KnowledgeOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default)
    {
        // Validate document size (UTF-8 byte count)
        long sizeBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (sizeBytes > _options.MaxDocumentSizeBytes)
        {
            throw new InvalidOperationException(
                $"Document '{title}' is {sizeBytes:N0} bytes, which exceeds the {_options.MaxDocumentSizeBytes:N0}-byte limit.");
        }

        // Validate document count
        if (_documents.Count >= _options.MaxDocuments)
        {
            throw new InvalidOperationException(
                $"Knowledge base is full ({_options.MaxDocuments} documents). Delete a document before uploading more.");
        }

        ct.ThrowIfCancellationRequested();

        string documentId = Guid.NewGuid().ToString("N")[..12];
        IReadOnlyList<DocumentChunker.DocumentChunk> chunks = DocumentChunker.Chunk(content);

        if (chunks.Count == 0)
        {
            _logger.LogWarning("Document '{Title}' produced no chunks — skipping", title);
            return Task.FromResult(documentId);
        }

        // Validate chunk count (check before ingesting)
        if (_chunks.Count + chunks.Count > _options.MaxChunks)
        {
            throw new InvalidOperationException(
                $"Ingesting '{title}' would produce {chunks.Count} chunks, exceeding the {_options.MaxChunks} chunk limit " +
                $"(current: {_chunks.Count}). Delete documents to free space.");
        }

        var doc = new IndexedDocument(documentId, title, source, DateTime.UtcNow, chunks.Count);
        _documents[documentId] = doc;

        foreach (DocumentChunker.DocumentChunk chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            string chunkId = $"{documentId}:{chunk.Index}";
            string[] tokens = Tokenize(chunk.Text);
            Dictionary<string, int> termFrequencies = BuildTermFrequencies(tokens);

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

        string[] queryTerms = Tokenize(query);
        if (queryTerms.Length == 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // Compute IDF for each query term
        var idfScores = new Dictionary<string, double>();
        foreach (string? term in queryTerms.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            int docsContaining = _chunks.Values.Count(c => c.TermFrequencies.ContainsKey(term));
            idfScores[term] = Math.Log(((_totalChunks - docsContaining + 0.5) / (docsContaining + 0.5)) + 1.0);
        }

        // Score each chunk via BM25
        var scored = new List<(IndexedChunk Chunk, double Score)>();
        foreach (IndexedChunk chunk in _chunks.Values)
        {
            ct.ThrowIfCancellationRequested();

            double score = 0.0;
            foreach (string term in queryTerms)
            {
                if (!chunk.TermFrequencies.TryGetValue(term, out int tf))
                    continue;

                double idf = idfScores.GetValueOrDefault(term, 0);
                double numerator = tf * (_k1 + 1);
                double denominator = tf + (_k1 * (1 - _b + (_b * chunk.TokenCount / _avgDocLength)));
                score += idf * (numerator / denominator);
            }

            if (score > _minScoreThreshold)
                scored.Add((chunk, score));
        }

        // Normalize scores to 0-1 range
        double maxScore = scored.Count > 0 ? scored.Max(s => s.Score) : 1.0;
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
        foreach (string? key in chunkKeys)
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

    /// <inheritdoc />
    public KnowledgeBaseCapabilities GetCapabilities() => new(
        ProviderName: ProviderName,
        Relevance: KnowledgeRelevanceKind.Lexical,
        Persistent: false,
        RequiresCloud: false,
        Quotas: new KnowledgeQuotas(
            MaxDocuments: _options.MaxDocuments,
            MaxChunks: _options.MaxChunks,
            MaxDocumentSizeBytes: _options.MaxDocumentSizeBytes),
        ScoreSemantics:
            "BM25 lexical score, normalized 0-1 within a single query response. " +
            "Scores are provider-local and not comparable across providers.");

    /// <inheritdoc />
    public Task ProbeAsync(CancellationToken ct = default)
    {
        // In-memory provider has no external dependency — it is always reachable
        // once the process is running. We honour cancellation for API parity.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string[] Tokenize(string text) =>
        [.. text.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '-', '—'))
            .Where(t => t.Length > 1)];

    private static Dictionary<string, int> BuildTermFrequencies(string[] tokens)
    {
        var tf = new Dictionary<string, int>();
        foreach (string token in tokens)
        {
            tf.TryGetValue(token, out int count);
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
