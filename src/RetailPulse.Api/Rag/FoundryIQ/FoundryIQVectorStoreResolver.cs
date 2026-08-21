using Microsoft.Extensions.Logging;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Resolves the bound Foundry vector store from
/// <see cref="FoundryIQOptions.VectorStoreId"/> (exact id) or
/// <see cref="FoundryIQOptions.VectorStoreName"/> (paged lookup). The
/// resolved id is cached per-process — vector stores in Foundry are named
/// resources and switching bindings is a deliberate configuration change, so
/// re-resolving on every search would waste tokens and quota.
/// </summary>
public sealed class FoundryIQVectorStoreResolver
{
    private readonly IFoundryIQClient _client;
    private readonly FoundryIQOptions _options;
    private readonly ILogger<FoundryIQVectorStoreResolver> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FoundryIQVectorStoreResolver(
        IFoundryIQClient client,
        FoundryIQOptions options,
        ILogger<FoundryIQVectorStoreResolver> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolves — and caches — the vector store id for the current binding.
    /// Throws <see cref="FoundryIQVectorStoreNotFoundException"/> when the
    /// configured name/id does not resolve to a real store, so the caller
    /// can translate to <see cref="Contracts.Rag.KnowledgeProviderUnavailableException"/>.
    /// </summary>
    public async Task<string> ResolveAsync(CancellationToken ct)
    {
        if (PeekResolvedId is not null)
        {
            return PeekResolvedId;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (PeekResolvedId is not null)
            {
                return PeekResolvedId;
            }

            if (!string.IsNullOrWhiteSpace(_options.VectorStoreId))
            {
                FoundryIQVectorStoreInfo info = await _client
                    .GetVectorStoreAsync(_options.VectorStoreId!, ct)
                    .ConfigureAwait(false);
                _log.LogInformation(
                    "Foundry IQ vector store bound by id '{Id}' (name '{Name}', status '{Status}').",
                    info.Id, info.Name, info.Status);
                PeekResolvedId = info.Id;
                return PeekResolvedId;
            }

            string name = _options.VectorStoreName!;
            await foreach (FoundryIQVectorStoreInfo candidate in _client
                .EnumerateVectorStoresAsync(ct)
                .ConfigureAwait(false))
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                _log.LogInformation(
                    "Foundry IQ vector store bound by name '{Name}' -> id '{Id}' (status '{Status}').",
                    name, candidate.Id, candidate.Status);
                PeekResolvedId = candidate.Id;
                return PeekResolvedId;
            }

            throw new FoundryIQVectorStoreNotFoundException(
                $"Foundry IQ vector store '{name}' was not found in the configured project. " +
                "Verify Knowledge:FoundryIQ:VectorStoreName matches a store visible to the managed identity, " +
                "or set Knowledge:FoundryIQ:VectorStoreId for an exact-id bind.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Diagnostic — reveals whether the store id has been resolved yet.</summary>
    internal string? PeekResolvedId { get; private set; }
}

/// <summary>
/// Raised when a configured Foundry IQ vector store name or id cannot be
/// resolved against the project. Translated by the knowledge base into
/// <see cref="Contracts.Rag.KnowledgeProviderUnavailableException"/>.
/// </summary>
public sealed class FoundryIQVectorStoreNotFoundException : Exception
{
    public FoundryIQVectorStoreNotFoundException(string message) : base(message) { }
}
