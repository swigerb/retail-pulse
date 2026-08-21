using Microsoft.Extensions.Logging;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Resolves — and lazily creates — the internal Foundry retrieval agent used
/// by <see cref="FoundryIQKnowledgeBase"/> to run file_search against the
/// bound vector store.
///
/// The retrieval agent is not a customer-facing agent: it exists solely so
/// the file_search tool can be invoked with a stable managed-identity binding
/// to <see cref="FoundryIQVectorStoreResolver"/>'s resolved store. Resolution
/// order:
/// <list type="number">
///   <item><description>If <see cref="FoundryIQOptions.RetrievalAgentId"/> is
///   set, that id is used unchanged (emergency direct bind).</description></item>
///   <item><description>Otherwise agents are enumerated and matched by
///   <see cref="FoundryIQOptions.RetrievalAgentName"/>.</description></item>
///   <item><description>Otherwise the agent is created with
///   <see cref="FoundryIQOptions.Model"/> + a fixed retrieval-bridge
///   instructions template.</description></item>
/// </list>
/// Resolution is cached per-process and serialised behind a semaphore so
/// concurrent first requests don't create duplicate agents.
/// </summary>
public sealed class FoundryIQRetrievalAgentProvider
{
    /// <summary>Fixed instructions template for the retrieval bridge agent. See ADR-013.</summary>
    public const string RetrievalInstructions =
        "You are Retail Pulse's file-search retrieval bridge. Every user turn is a search query. " +
        "Use the file_search tool exactly once, return only the tool's results, and reply with the single word 'ok' as the assistant message.";

    private readonly IFoundryIQClient _client;
    private readonly FoundryIQVectorStoreResolver _resolver;
    private readonly FoundryIQOptions _options;
    private readonly ILogger<FoundryIQRetrievalAgentProvider> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FoundryIQRetrievalAgentProvider(
        IFoundryIQClient client,
        FoundryIQVectorStoreResolver resolver,
        FoundryIQOptions options,
        ILogger<FoundryIQRetrievalAgentProvider> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Resolves (and caches) the retrieval agent id for the bound vector store.</summary>
    public async Task<string> GetOrCreateAsync(CancellationToken ct)
    {
        if (PeekResolvedAgentId is not null)
        {
            return PeekResolvedAgentId;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (PeekResolvedAgentId is not null)
            {
                return PeekResolvedAgentId;
            }

            if (!string.IsNullOrWhiteSpace(_options.RetrievalAgentId))
            {
                PeekResolvedAgentId = _options.RetrievalAgentId;
                _log.LogInformation("Foundry IQ retrieval agent bound by id '{Id}'.", PeekResolvedAgentId);
                return PeekResolvedAgentId;
            }

            string name = _options.RetrievalAgentName;
            await foreach (FoundryIQAgentInfo candidate in _client
                .EnumerateAgentsAsync(ct)
                .ConfigureAwait(false))
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    PeekResolvedAgentId = candidate.Id;
                    _log.LogInformation(
                        "Foundry IQ retrieval agent resolved by name '{Name}' -> id '{Id}'.",
                        name, PeekResolvedAgentId);
                    return PeekResolvedAgentId;
                }
            }

            string vectorStoreId = await _resolver.ResolveAsync(ct).ConfigureAwait(false);
            PeekResolvedAgentId = await _client
                .CreateRetrievalAgentAsync(_options.Model, name, RetrievalInstructions, vectorStoreId, ct)
                .ConfigureAwait(false);
            _log.LogInformation(
                "Foundry IQ retrieval agent created: name '{Name}', id '{Id}', vector store '{VectorStoreId}'.",
                name, PeekResolvedAgentId, vectorStoreId);
            return PeekResolvedAgentId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Diagnostic — reveals whether the retrieval agent id has been resolved yet.</summary>
    internal string? PeekResolvedAgentId { get; private set; }
}
