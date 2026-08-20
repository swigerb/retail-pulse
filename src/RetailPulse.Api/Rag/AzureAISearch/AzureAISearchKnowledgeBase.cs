using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using RetailPulse.Api.Configuration;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Durable, semantically-retrievable <see cref="IKnowledgeBase"/> backed by
/// Azure AI Search. Uses the same <see cref="DocumentChunker"/> as the in-memory
/// provider so chunk boundaries stay consistent across providers.
///
/// Retrieval combines vector similarity (via APIM-generated embeddings) with
/// lexical BM25 in a hybrid query. When
/// <see cref="AzureAISearchOptions.SemanticRankingEnabled"/> is true, the
/// hybrid result set is reranked by Azure AI Search's semantic ranker.
///
/// Score semantics are provider-local: hybrid + semantic scores are not
/// comparable to the in-memory BM25 score. Callers must respect
/// <see cref="KnowledgeBaseCapabilities.ScoreSemantics"/> and never cross-rank
/// results from different providers.
/// </summary>
public sealed class AzureAISearchKnowledgeBase : IKnowledgeBase
{
    /// <summary>Stable name reported in <see cref="GetCapabilities"/>.</summary>
    public const string ProviderName = "AzureAISearch";

    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly ApimEmbeddingClient _embeddingClient;
    private readonly AzureAISearchOptions _options;
    private readonly KnowledgeOptions _quotas;
    private readonly ILogger<AzureAISearchKnowledgeBase> _logger;
    private readonly SemaphoreSlim _indexInit = new(1, 1);
    private bool _indexEnsured;

    public AzureAISearchKnowledgeBase(
        SearchIndexClient indexClient,
        SearchClient searchClient,
        ApimEmbeddingClient embeddingClient,
        AzureAISearchOptions options,
        KnowledgeOptions quotas,
        ILogger<AzureAISearchKnowledgeBase> logger)
    {
        _indexClient = indexClient ?? throw new ArgumentNullException(nameof(indexClient));
        _searchClient = searchClient ?? throw new ArgumentNullException(nameof(searchClient));
        _embeddingClient = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _quotas = quotas ?? throw new ArgumentNullException(nameof(quotas));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public KnowledgeBaseCapabilities GetCapabilities() => new(
        ProviderName: ProviderName,
        Relevance: _options.SemanticRankingEnabled
            ? KnowledgeRelevanceKind.Hybrid
            : KnowledgeRelevanceKind.Hybrid,
        Persistent: true,
        RequiresCloud: true,
        Quotas: new KnowledgeQuotas(
            MaxDocuments: _quotas.MaxDocuments,
            MaxChunks: _quotas.MaxChunks,
            MaxDocumentSizeBytes: _quotas.MaxDocumentSizeBytes),
        ScoreSemantics: _options.SemanticRankingEnabled
            ? "Hybrid vector + BM25 score reranked by Azure AI Search semantic ranker. Higher is better. Scores are provider-local and NOT comparable across providers."
            : "Hybrid vector + BM25 score via Reciprocal Rank Fusion. Higher is better. Scores are provider-local and NOT comparable across providers.");

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureIndexAsync(ct).ConfigureAwait(false);
        }
        catch (KnowledgeProviderUnavailableException)
        {
            throw;
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search probe failed at transport: {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        long sizeBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (sizeBytes > _quotas.MaxDocumentSizeBytes)
        {
            throw new InvalidOperationException(
                $"Document '{title}' is {sizeBytes:N0} bytes, which exceeds the {_quotas.MaxDocumentSizeBytes:N0}-byte limit.");
        }

        await EnsureIndexAsync(ct).ConfigureAwait(false);

        IReadOnlyList<DocumentChunker.DocumentChunk> chunks = DocumentChunker.Chunk(content);
        string documentId = Guid.NewGuid().ToString("N")[..12];
        if (chunks.Count == 0)
        {
            _logger.LogWarning("Document '{Title}' produced no chunks — skipping", title);
            return documentId;
        }

        // Precompute embeddings for every chunk in a single batch to minimize
        // APIM round-trips and preserve chunk-order deterministically.
        List<string> chunkTexts = [.. chunks.Select(c => c.Text)];
        IReadOnlyList<ReadOnlyMemory<float>> embeddings;
        try
        {
            embeddings = await _embeddingClient.EmbedBatchAsync(chunkTexts, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Failed to compute embeddings for document '{title}': {ex.Message}",
                ex);
        }

        DateTimeOffset ingestedAt = DateTimeOffset.UtcNow;
        var actions = new List<IndexDocumentsAction<SearchDocument>>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            DocumentChunker.DocumentChunk chunk = chunks[i];
            string chunkId = $"{documentId}_{i:D4}";
            var doc = new SearchDocument
            {
                [AzureAISearchIndexSchema.ChunkIdField] = chunkId,
                [AzureAISearchIndexSchema.DocumentIdField] = documentId,
                [AzureAISearchIndexSchema.ChunkIndexField] = chunk.Index,
                [AzureAISearchIndexSchema.TitleField] = title,
                [AzureAISearchIndexSchema.ContentField] = chunk.Text,
                [AzureAISearchIndexSchema.SourceField] = source,
                [AzureAISearchIndexSchema.SectionHeaderField] = chunk.SectionHeader ?? string.Empty,
                [AzureAISearchIndexSchema.IngestedAtField] = ingestedAt,
                [AzureAISearchIndexSchema.SchemaVersionField] = _options.SchemaVersion,
                [AzureAISearchIndexSchema.AgentScopeField] = Array.Empty<string>(),
                [AzureAISearchIndexSchema.VectorField] = embeddings[i].ToArray(),
            };
            actions.Add(IndexDocumentsAction.MergeOrUpload(doc));
        }

        try
        {
            Response<IndexDocumentsResult> result = await _searchClient.IndexDocumentsAsync(
                IndexDocumentsBatch.Create(actions.ToArray()),
                cancellationToken: ct).ConfigureAwait(false);

            var failures = result.Value.Results.Where(r => !r.Succeeded).ToList();
            if (failures.Count > 0)
            {
                string first = failures[0].ErrorMessage ?? "unknown";
                throw new InvalidOperationException(
                    $"Azure AI Search rejected {failures.Count}/{chunks.Count} chunk uploads for document '{title}'. First error: {first}");
            }
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search ingest failed at transport for document '{title}': {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }

        _logger.LogInformation(
            "Ingested document '{Title}' ({Source}) into Azure AI Search index '{Index}': {ChunkCount} chunks, id={Id}",
            title, source, _options.IndexName, chunks.Count, documentId);

        return documentId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await EnsureIndexAsync(ct).ConfigureAwait(false);

        ReadOnlyMemory<float> queryVector;
        try
        {
            queryVector = await _embeddingClient.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Failed to compute query embedding: {ex.Message}",
                ex);
        }

        var searchOptions = new SearchOptions
        {
            Size = topK,
            IncludeTotalCount = false,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVector)
                    {
                        KNearestNeighborsCount = Math.Max(topK, 50),
                        Fields = { AzureAISearchIndexSchema.VectorField },
                    },
                },
            },
        };
        searchOptions.Select.Add(AzureAISearchIndexSchema.ChunkIdField);
        searchOptions.Select.Add(AzureAISearchIndexSchema.DocumentIdField);
        searchOptions.Select.Add(AzureAISearchIndexSchema.ChunkIndexField);
        searchOptions.Select.Add(AzureAISearchIndexSchema.TitleField);
        searchOptions.Select.Add(AzureAISearchIndexSchema.ContentField);
        searchOptions.Select.Add(AzureAISearchIndexSchema.SourceField);
        searchOptions.SearchFields.Add(AzureAISearchIndexSchema.TitleField);
        searchOptions.SearchFields.Add(AzureAISearchIndexSchema.ContentField);

        if (_options.SemanticRankingEnabled)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = _options.SemanticConfigurationName,
            };
        }

        SearchResults<SearchDocument> raw;
        try
        {
            Response<SearchResults<SearchDocument>> response = await _searchClient
                .SearchAsync<SearchDocument>(query, searchOptions, ct)
                .ConfigureAwait(false);
            raw = response.Value;
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search query failed at transport: {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search index '{_options.IndexName}' returned 404 during query. Confirm the index exists or set Knowledge:AzureAISearch:AutoCreateIndex=true.",
                ex);
        }

        var results = new List<SearchResult>(topK);
        await foreach (Azure.Search.Documents.Models.SearchResult<SearchDocument> hit in raw.GetResultsAsync().ConfigureAwait(false))
        {
            SearchDocument doc = hit.Document;
            double score = ResolveScore(hit);

            results.Add(new SearchResult(
                DocumentId: doc.TryGetValue(AzureAISearchIndexSchema.DocumentIdField, out object? docId) ? docId?.ToString() ?? string.Empty : string.Empty,
                Title: doc.TryGetValue(AzureAISearchIndexSchema.TitleField, out object? title) ? title?.ToString() ?? string.Empty : string.Empty,
                Chunk: doc.TryGetValue(AzureAISearchIndexSchema.ContentField, out object? content) ? content?.ToString() ?? string.Empty : string.Empty,
                Score: score,
                Source: doc.TryGetValue(AzureAISearchIndexSchema.SourceField, out object? source) ? source?.ToString() ?? string.Empty : string.Empty,
                ChunkIndex: doc.TryGetValue(AzureAISearchIndexSchema.ChunkIndexField, out object? chunkIndex) && chunkIndex is not null
                    ? Convert.ToInt32(chunkIndex, System.Globalization.CultureInfo.InvariantCulture)
                    : 0));

            if (results.Count >= topK)
            {
                break;
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct).ConfigureAwait(false);

        var options = new SearchOptions
        {
            Size = Math.Max(_quotas.MaxDocuments * 4, 200),
            IncludeTotalCount = false,
        };
        options.Select.Add(AzureAISearchIndexSchema.DocumentIdField);
        options.Select.Add(AzureAISearchIndexSchema.TitleField);
        options.Select.Add(AzureAISearchIndexSchema.SourceField);
        options.Select.Add(AzureAISearchIndexSchema.IngestedAtField);
        options.OrderBy.Add($"{AzureAISearchIndexSchema.IngestedAtField} desc");

        SearchResults<SearchDocument> raw;
        try
        {
            Response<SearchResults<SearchDocument>> response = await _searchClient
                .SearchAsync<SearchDocument>("*", options, ct)
                .ConfigureAwait(false);
            raw = response.Value;
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search list failed at transport: {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }

        var docs = new Dictionary<string, DocumentAggregate>(StringComparer.OrdinalIgnoreCase);
        await foreach (Azure.Search.Documents.Models.SearchResult<SearchDocument> hit in raw.GetResultsAsync().ConfigureAwait(false))
        {
            SearchDocument doc = hit.Document;
            if (!doc.TryGetValue(AzureAISearchIndexSchema.DocumentIdField, out object? idValue) || idValue is null)
            {
                continue;
            }
            string documentId = idValue.ToString()!;
            string title = doc.TryGetValue(AzureAISearchIndexSchema.TitleField, out object? t) ? t?.ToString() ?? string.Empty : string.Empty;
            string source = doc.TryGetValue(AzureAISearchIndexSchema.SourceField, out object? s) ? s?.ToString() ?? string.Empty : string.Empty;
            DateTime ingestedAt = doc.TryGetValue(AzureAISearchIndexSchema.IngestedAtField, out object? ia) && ia is DateTimeOffset dto
                ? dto.UtcDateTime
                : DateTime.UtcNow;

            if (docs.TryGetValue(documentId, out DocumentAggregate? agg))
            {
                agg.ChunkCount++;
                if (ingestedAt < agg.IngestedAt) agg.IngestedAt = ingestedAt;
            }
            else
            {
                docs[documentId] = new DocumentAggregate
                {
                    Id = documentId,
                    Title = title,
                    Source = source,
                    IngestedAt = ingestedAt,
                    ChunkCount = 1,
                };
            }
        }

        return [.. docs.Values
            .OrderByDescending(d => d.IngestedAt)
            .Select(d => new DocumentInfo(d.Id, d.Title, d.Source, d.IngestedAt, d.ChunkCount))];
    }

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        await EnsureIndexAsync(ct).ConfigureAwait(false);

        var listOptions = new SearchOptions
        {
            Size = _quotas.MaxChunks,
            Filter = $"{AzureAISearchIndexSchema.DocumentIdField} eq '{EscapeODataString(documentId)}'",
        };
        listOptions.Select.Add(AzureAISearchIndexSchema.ChunkIdField);

        SearchResults<SearchDocument> raw;
        try
        {
            Response<SearchResults<SearchDocument>> response = await _searchClient
                .SearchAsync<SearchDocument>(searchText: null, listOptions, ct)
                .ConfigureAwait(false);
            raw = response.Value;
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search delete-lookup failed at transport: {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }

        var chunkIds = new List<string>();
        await foreach (Azure.Search.Documents.Models.SearchResult<SearchDocument> hit in raw.GetResultsAsync().ConfigureAwait(false))
        {
            if (hit.Document.TryGetValue(AzureAISearchIndexSchema.ChunkIdField, out object? id) && id is not null)
            {
                chunkIds.Add(id.ToString()!);
            }
        }

        if (chunkIds.Count == 0)
        {
            _logger.LogInformation("Delete requested for unknown documentId {DocumentId} — no chunks removed", documentId);
            return;
        }

        var deleteActions = chunkIds
            .Select(id =>
            {
                var d = new SearchDocument { [AzureAISearchIndexSchema.ChunkIdField] = id };
                return IndexDocumentsAction.Delete(d);
            })
            .ToArray();

        try
        {
            await _searchClient.IndexDocumentsAsync(
                IndexDocumentsBatch.Create(deleteActions),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (IsTransport(ex))
        {
            throw new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Azure AI Search delete failed at transport for document '{documentId}': {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                ex);
        }

        _logger.LogInformation("Deleted document {DocumentId} ({ChunkCount} chunks) from Azure AI Search", documentId, chunkIds.Count);
    }

    private static double ResolveScore(Azure.Search.Documents.Models.SearchResult<SearchDocument> hit)
    {
        // Semantic reranker scores appear on SemanticSearch.RerankerScore when
        // semantic ranking is enabled; otherwise the hybrid RRF Score is used.
        double? reranker = hit.SemanticSearch?.RerankerScore;
        double raw = reranker ?? hit.Score ?? 0.0;
        return raw < 0 ? 0.0 : raw;
    }

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsTransport(RequestFailedException ex) =>
        ex.Status == 0 ||
        ex.Status == 408 ||
        ex.Status == 429 ||
        ex.Status >= 500;

    /// <summary>
    /// Ensures the target index exists and its schema matches this build.
    /// Runs at most once per process on the happy path; a probe failure resets
    /// the latch so a later ProbeAsync/data call retries.
    /// </summary>
    internal async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (_indexEnsured)
        {
            return;
        }

        await _indexInit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_indexEnsured)
            {
                return;
            }

            SearchIndex? existing = null;
            try
            {
                Response<SearchIndex> response = await _indexClient.GetIndexAsync(_options.IndexName, ct).ConfigureAwait(false);
                existing = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                existing = null;
            }
            catch (RequestFailedException ex) when (IsTransport(ex))
            {
                throw new KnowledgeProviderUnavailableException(
                    ProviderName,
                    $"Azure AI Search unreachable while inspecting index '{_options.IndexName}': {ex.Status}",
                    ex);
            }

            if (existing is null)
            {
                if (!_options.AutoCreateIndex)
                {
                    throw new KnowledgeProviderUnavailableException(
                        ProviderName,
                        $"Azure AI Search index '{_options.IndexName}' does not exist and AutoCreateIndex is false.");
                }

                SearchIndex desired = AzureAISearchIndexSchema.Build(_options);
                try
                {
                    await _indexClient.CreateIndexAsync(desired, ct).ConfigureAwait(false);
                }
                catch (RequestFailedException ex)
                {
                    throw new KnowledgeProviderUnavailableException(
                        ProviderName,
                        $"Failed to create Azure AI Search index '{_options.IndexName}': {ex.Status} {ex.ErrorCode ?? "n/a"}.",
                        ex);
                }
                _logger.LogInformation(
                    "Created Azure AI Search index '{Index}' (dim={Dim}, semantic={Semantic}, schemaVersion={SchemaVersion})",
                    _options.IndexName, _options.Embeddings.Dimensions,
                    _options.SemanticRankingEnabled, _options.SchemaVersion);
            }
            else
            {
                string? mismatch = AzureAISearchIndexSchema.DetectMismatch(existing, _options);
                if (mismatch is not null)
                {
                    throw new KnowledgeProviderUnavailableException(
                        ProviderName,
                        $"{mismatch} Follow docs/rag/azure-ai-search-index.md to reindex — the provider will not silently corrupt an existing index.");
                }
            }

            _indexEnsured = true;
        }
        finally
        {
            _indexInit.Release();
        }
    }

    private sealed class DocumentAggregate
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime IngestedAt { get; set; }
        public int ChunkCount { get; set; }
    }
}
