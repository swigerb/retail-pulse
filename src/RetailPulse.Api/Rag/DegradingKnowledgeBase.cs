using RetailPulse.Contracts.Rag;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Decorator that applies the configured
/// <see cref="KnowledgeDegradationMode"/> around a primary
/// <see cref="IKnowledgeBase"/>.
///
/// Contract:
/// <list type="bullet">
///   <item><see cref="ProbeAsync"/> runs the primary's health probe. On
///     <see cref="KnowledgeProviderUnavailableException"/>: in
///     <see cref="KnowledgeDegradationMode.FallbackToInMemory"/> it logs a
///     prominent warning and permanently swaps the active provider to the
///     in-memory fallback; in <see cref="KnowledgeDegradationMode.FailLoud"/>
///     the exception propagates and startup fails.</item>
///   <item>Data-plane calls (search/ingest/list/delete) propagate all provider
///     exceptions to the caller in <see cref="KnowledgeDegradationMode.FailLoud"/>.
///     In <see cref="KnowledgeDegradationMode.FallbackToInMemory"/>, a
///     <see cref="KnowledgeProviderUnavailableException"/> from the primary is
///     re-tried against the fallback for that single request; other exceptions
///     still propagate.</item>
/// </list>
/// The decorator NEVER swallows an exception to return an empty result. When
/// fallback is active the caller sees the fallback's genuine response; when
/// fallback is disabled the caller sees the failure.
/// </summary>
public sealed class DegradingKnowledgeBase : IKnowledgeBase
{
    private readonly IKnowledgeBase _primary;
    private readonly IKnowledgeBase _fallback;
    private readonly KnowledgeDegradationMode _degradation;
    private readonly ILogger<DegradingKnowledgeBase> _logger;

    // Startup-probe outcome. Until ProbeAsync has run, data-plane calls go to
    // the primary; the runtime-exception fallback still applies per request.
    private IKnowledgeBase _active;

    public DegradingKnowledgeBase(
        IKnowledgeBase primary,
        IKnowledgeBase fallback,
        KnowledgeDegradationMode degradation,
        ILogger<DegradingKnowledgeBase> logger)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _degradation = degradation;
        _logger = logger;
        _active = primary;
    }

    /// <summary>
    /// Provider name currently serving traffic after the startup probe (either
    /// the primary or the in-memory fallback). Prior to <see cref="ProbeAsync"/>
    /// this is the primary's name. Useful for observability endpoints.
    /// </summary>
    public string ActiveProviderName => _active.GetCapabilities().ProviderName;

    /// <summary>
    /// True when the startup probe caused the primary to be permanently
    /// replaced by the in-memory fallback for this process lifetime.
    /// </summary>
    public bool PrimaryReplacedByFallback { get; private set; }

    /// <summary>Configured degradation policy (for observability).</summary>
    public KnowledgeDegradationMode DegradationMode => _degradation;

    /// <inheritdoc />
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await _primary.ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (KnowledgeProviderUnavailableException ex)
        {
            string providerName = SafeProviderName(_primary);
            if (_degradation == KnowledgeDegradationMode.FallbackToInMemory)
            {
                _logger.LogWarning(ex,
                    "Knowledge provider '{Provider}' failed startup probe — swapping to in-memory fallback " +
                    "per configured degradation policy (FallbackToInMemory). Subsequent requests are served " +
                    "by the in-memory BM25 corpus, not the configured backend.",
                    providerName);
                _active = _fallback;
                PrimaryReplacedByFallback = true;
                return;
            }

            _logger.LogError(ex,
                "Knowledge provider '{Provider}' failed startup probe. FailLoud policy — startup aborted.",
                providerName);
            throw;
        }
    }

    /// <inheritdoc />
    public KnowledgeBaseCapabilities GetCapabilities() => _active.GetCapabilities();

    /// <inheritdoc />
    public Task<string> IngestDocumentAsync(string title, string content, string source, CancellationToken ct = default) =>
        WithFallbackAsync(kb => kb.IngestDocumentAsync(title, content, source, ct), nameof(IngestDocumentAsync));

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
        WithFallbackAsync(kb => kb.SearchAsync(query, topK, ct), nameof(SearchAsync));

    /// <inheritdoc />
    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(CancellationToken ct = default) =>
        WithFallbackAsync(kb => kb.ListDocumentsAsync(ct), nameof(ListDocumentsAsync));

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
        WithFallbackAsync(kb => kb.DeleteDocumentAsync(documentId, ct), nameof(DeleteDocumentAsync));

    private async Task<TResult> WithFallbackAsync<TResult>(Func<IKnowledgeBase, Task<TResult>> operation, string opName)
    {
        try
        {
            return await operation(_active).ConfigureAwait(false);
        }
        catch (KnowledgeProviderUnavailableException ex)
            when (_degradation == KnowledgeDegradationMode.FallbackToInMemory
                  && !ReferenceEquals(_active, _fallback))
        {
            _logger.LogWarning(ex,
                "Knowledge provider '{Provider}' unavailable during {Operation} — serving this request from " +
                "the in-memory fallback (primary remains the configured provider for future requests).",
                ex.ProviderName, opName);
            return await operation(_fallback).ConfigureAwait(false);
        }
    }

    private async Task WithFallbackAsync(Func<IKnowledgeBase, Task> operation, string opName)
    {
        try
        {
            await operation(_active).ConfigureAwait(false);
        }
        catch (KnowledgeProviderUnavailableException ex)
            when (_degradation == KnowledgeDegradationMode.FallbackToInMemory
                  && !ReferenceEquals(_active, _fallback))
        {
            _logger.LogWarning(ex,
                "Knowledge provider '{Provider}' unavailable during {Operation} — serving this request from " +
                "the in-memory fallback (primary remains the configured provider for future requests).",
                ex.ProviderName, opName);
            await operation(_fallback).ConfigureAwait(false);
        }
    }

    private static string SafeProviderName(IKnowledgeBase kb)
    {
        try { return kb.GetCapabilities().ProviderName; }
        catch { return kb.GetType().Name; }
    }
}
