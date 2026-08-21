using Azure;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Resilience;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// <see cref="IKnowledgeBase"/> backed by the Foundry <em>file_search</em>
/// tool bound to a named Foundry-managed vector store (issue #104).
///
/// This provider is <b>read-only from Retail Pulse's perspective</b>: the
/// corpus is owned outside the application (Foundry-managed vector store),
/// so <see cref="IngestDocumentAsync"/> and <see cref="DeleteDocumentAsync"/>
/// throw <see cref="NotSupportedException"/> with a documented, actionable
/// message. This is reported honestly through
/// <see cref="KnowledgeBaseCapabilities.SupportsMutation"/> — the shared
/// conformance suite gates its mutation assertions on that flag, and a
/// dedicated read-only conformance test asserts the throw. See ADR-013.
///
/// Resilience: every SDK call chain is wrapped in a bounded timeout
/// (<see cref="FoundryIQOptions.RequestTimeoutMs"/>) and a Polly circuit
/// breaker (5 handled failures / 30s sampling / 30s open) that reports state
/// to <see cref="CircuitBreakerHealthCheck.ReportFoundryIqState"/>.
///
/// Cost attribution: model-side tokens exposed by
/// <c>ThreadRun.Usage</c> are recorded through <see cref="ICostTracker"/> as
/// a <see cref="UsageEvent"/> with <c>ToolName = "file_search"</c>. Foundry
/// vector-store storage and retrieval-side costs are NOT observable from the
/// SDK — that gap is documented in ADR-013 and the operator guide.
/// </summary>
public sealed class FoundryIQKnowledgeBase : IKnowledgeBase
{
    /// <summary>Stable name reported in <see cref="GetCapabilities"/>.</summary>
    public const string ProviderName = "FoundryIQ";

    /// <summary>
    /// Verbatim string returned to callers so the mutation-unsupported contract
    /// is discoverable at runtime and searchable in support diagnostics.
    /// </summary>
    public const string MutationUnsupportedMessage =
        "Foundry IQ knowledge provider is read-only: its corpus is a Foundry-managed vector store owned outside Retail Pulse. " +
        "Manage documents via the Foundry portal or an ingest pipeline (out of scope for #104) and re-run search.";

    private readonly IFoundryIQClient _client;
    private readonly FoundryIQVectorStoreResolver _resolver;
    private readonly FoundryIQRetrievalAgentProvider _agentProvider;
    private readonly FoundryIQOptions _options;
    private readonly KnowledgeOptions _quotas;
    private readonly ICostTracker _costTracker;
    private readonly ILogger<FoundryIQKnowledgeBase> _log;
    private readonly ResiliencePipeline _pipeline;

    public FoundryIQKnowledgeBase(
        IFoundryIQClient client,
        FoundryIQVectorStoreResolver resolver,
        FoundryIQRetrievalAgentProvider agentProvider,
        FoundryIQOptions options,
        KnowledgeOptions quotas,
        ICostTracker costTracker,
        ILogger<FoundryIQKnowledgeBase> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agentProvider = agentProvider ?? throw new ArgumentNullException(nameof(agentProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _quotas = quotas ?? throw new ArgumentNullException(nameof(quotas));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                OnOpened = _ =>
                {
                    CircuitBreakerHealthCheck.ReportFoundryIqState(CircuitBreakerState.Open);
                    _log.LogWarning("Foundry IQ retrieval circuit opened.");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    CircuitBreakerHealthCheck.ReportFoundryIqState(CircuitBreakerState.Closed);
                    _log.LogInformation("Foundry IQ retrieval circuit closed.");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ =>
                {
                    CircuitBreakerHealthCheck.ReportFoundryIqState(CircuitBreakerState.HalfOpen);
                    _log.LogInformation("Foundry IQ retrieval circuit half-open.");
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    /// <inheritdoc />
    public KnowledgeBaseCapabilities GetCapabilities() => new(
        ProviderName: ProviderName,
        Relevance: KnowledgeRelevanceKind.Semantic,
        Persistent: true,
        RequiresCloud: true,
        Quotas: new KnowledgeQuotas(
            MaxDocuments: _quotas.MaxDocuments,
            MaxChunks: _quotas.MaxChunks,
            MaxDocumentSizeBytes: _quotas.MaxDocumentSizeBytes),
        ScoreSemantics:
            "Foundry file_search score in [0..1]. Higher is better. Scores are provider-local and NOT comparable across providers. " +
            "ChunkIndex is a per-query rank ordinal (0-based) — Foundry does not expose a stable chunk id, so ChunkIndex must not be used as a durable identifier.",
        SupportsMutation: false);

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource linked = LinkTimeout(ct);
        try
        {
            await _pipeline
                .ExecuteAsync(async token =>
                {
                    string storeId = await _resolver.ResolveAsync(token).ConfigureAwait(false);
                    _ = await _client.GetVectorStoreAsync(storeId, token).ConfigureAwait(false);
                }, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not KnowledgeProviderUnavailableException)
        {
            throw TranslateAvailability(ex, "probe");
        }
    }

    /// <inheritdoc />
    public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) =>
        throw new NotSupportedException(MutationUnsupportedMessage);

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
        throw new NotSupportedException(MutationUnsupportedMessage);

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
        SearchAsync(query, topK, sources: null, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int topK,
        IReadOnlyCollection<string>? sources,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (topK <= 0)
        {
            return [];
        }

        int effectiveTopK = Math.Min(topK, _options.MaxResults);
        using CancellationTokenSource linked = LinkTimeout(ct);

        FoundryIQSearchRunResult runResult;
        try
        {
            runResult = await _pipeline
                .ExecuteAsync(async token =>
                {
                    string agentId = await _agentProvider.GetOrCreateAsync(token).ConfigureAwait(false);
                    return await _client
                        .RunFileSearchAsync(agentId, query, _options.PollIntervalMs, token)
                        .ConfigureAwait(false);
                }, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not KnowledgeProviderUnavailableException)
        {
            throw TranslateAvailability(ex, "search");
        }

        await RecordCostAsync(runResult, ct).ConfigureAwait(false);

        IEnumerable<FoundryIQSearchHit> hitsEnumerable = runResult.Hits;
        if (sources is { Count: > 0 })
        {
            var allowed = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
            hitsEnumerable = hitsEnumerable.Where(h => allowed.Contains(h.FileName));
        }

        var results = new List<SearchResult>(effectiveTopK);
        int rank = 0;
        foreach (FoundryIQSearchHit hit in hitsEnumerable)
        {
            if (results.Count >= effectiveTopK)
            {
                break;
            }
            results.Add(new SearchResult(
                DocumentId: hit.FileId,
                Title: hit.FileName,
                Chunk: hit.Chunk,
                Score: hit.Score,
                Source: hit.FileName,
                ChunkIndex: rank++));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource linked = LinkTimeout(ct);
        List<FoundryIQVectorStoreFileInfo> fileIds;
        try
        {
            fileIds = await _pipeline
                .ExecuteAsync(async token =>
                {
                    string storeId = await _resolver.ResolveAsync(token).ConfigureAwait(false);
                    var collected = new List<FoundryIQVectorStoreFileInfo>();
                    await foreach (FoundryIQVectorStoreFileInfo file in _client
                        .EnumerateVectorStoreFilesAsync(storeId, token)
                        .ConfigureAwait(false))
                    {
                        collected.Add(file);
                    }
                    return collected;
                }, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not KnowledgeProviderUnavailableException)
        {
            throw TranslateAvailability(ex, "list");
        }

        var docs = new List<DocumentInfo>(fileIds.Count);
        foreach (FoundryIQVectorStoreFileInfo entry in fileIds)
        {
            FoundryIQFileInfo? info;
            try
            {
                info = await _client.GetFileAsync(entry.FileId, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not KnowledgeProviderUnavailableException)
            {
                throw TranslateAvailability(ex, "list");
            }

            docs.Add(new DocumentInfo(
                Id: entry.FileId,
                Title: info?.Filename ?? entry.FileId,
                Source: info?.Filename ?? entry.FileId,
                IngestedAt: info?.CreatedAt ?? DateTime.UtcNow,
                ChunkCount: 1));
        }

        return docs;
    }

    private CancellationTokenSource LinkTimeout(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMilliseconds(_options.RequestTimeoutMs));
        return linked;
    }

    private Task RecordCostAsync(FoundryIQSearchRunResult runResult, CancellationToken ct)
    {
        if (!runResult.UsageReported || (runResult.PromptTokens <= 0 && runResult.CompletionTokens <= 0))
        {
            _log.LogDebug("Foundry IQ retrieval run did not report token usage — skipping cost event.");
            return Task.CompletedTask;
        }

        var usage = new UsageEvent(
            AgentId: _options.CostTrackingAgentId,
            Model: _options.Model,
            InputTokens: runResult.PromptTokens,
            OutputTokens: runResult.CompletionTokens,
            ToolName: "file_search",
            Timestamp: DateTime.UtcNow);
        return _costTracker.TrackUsageAsync(usage, ct);
    }

    private KnowledgeProviderUnavailableException TranslateAvailability(Exception ex, string operation)
    {
        return ex switch
        {
            FoundryIQVectorStoreNotFoundException notFound => new KnowledgeProviderUnavailableException(
                ProviderName, notFound.Message, notFound),
            FoundryIQRunFailedException runFailed => new KnowledgeProviderUnavailableException(
                ProviderName, $"Foundry IQ {operation} failed: {runFailed.Message}", runFailed),
            RequestFailedException rfe when rfe.Status is 401 or 403 => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ {operation} unauthorized ({rfe.Status}). Confirm the managed identity has 'Azure AI Developer' on the project.",
                rfe),
            RequestFailedException rfe when rfe.Status == 404 => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ {operation} returned 404. Verify the project endpoint and the bound vector store/agent still exist.",
                rfe),
            RequestFailedException rfe => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ {operation} failed at transport: {rfe.Status} {rfe.ErrorCode ?? "n/a"}.",
                rfe),
            BrokenCircuitException broken => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ retrieval circuit is open — {operation} short-circuited to protect downstream capacity.",
                broken),
            OperationCanceledException canceled => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ {operation} timed out after {_options.RequestTimeoutMs}ms.",
                canceled),
            _ => new KnowledgeProviderUnavailableException(
                ProviderName,
                $"Foundry IQ {operation} failed: {ex.GetType().Name}: {ex.Message}",
                ex),
        };
    }
}
