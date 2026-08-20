using System.ComponentModel.DataAnnotations;

namespace RetailPulse.Api.Rag.AzureAISearch;

/// <summary>
/// Configuration surface for the optional Azure AI Search knowledge provider
/// (issue #103). Bound to the <c>Knowledge:AzureAISearch</c> section.
///
/// The provider is FULLY OPTIONAL. When <see cref="Endpoint"/> is blank the
/// provider is not registered and no Azure.Search SDK client, HTTP handler,
/// or credential is materialized — the default in-memory BM25 path stays
/// byte-for-byte unchanged. See docs/adr/012-azure-ai-search-provider.md.
/// </summary>
public sealed class AzureAISearchOptions
{
    /// <summary>Configuration section: <c>Knowledge:AzureAISearch</c>.</summary>
    public const string SectionName = "Knowledge:AzureAISearch";

    /// <summary>Fully-qualified Azure AI Search endpoint (e.g. <c>https://mysearch.search.windows.net</c>).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Index name used for chunk documents.</summary>
    public string IndexName { get; set; } = "retail-pulse-knowledge";

    /// <summary>
    /// Automatically create the target index at startup if it does not exist. The
    /// creation call is idempotent and never touches an existing index — a schema
    /// mismatch is surfaced by <see cref="Contracts.Rag.KnowledgeProviderUnavailableException"/>
    /// so operators run the documented reindex procedure explicitly.
    /// </summary>
    public bool AutoCreateIndex { get; set; } = true;

    /// <summary>
    /// Schema version stamped on every ingested document so a targeted reindex
    /// can identify chunks written by a prior schema shape. Any change to the
    /// index fields MUST bump this string.
    /// </summary>
    public string SchemaVersion { get; set; } = "v1";

    /// <summary>Per-request timeout applied to Search SDK calls (milliseconds).</summary>
    [Range(500, 120_000)]
    public int RequestTimeoutMs { get; set; } = 20_000;

    /// <summary>
    /// Enable semantic ranking on hybrid queries. Requires the Search service
    /// to be provisioned with the semanticSearch SKU (Free tier is included on
    /// Basic+ services). When disabled, the provider still performs hybrid
    /// lexical + vector ranking without the semantic reranker.
    /// </summary>
    public bool SemanticRankingEnabled { get; set; }

    /// <summary>Semantic configuration name declared on the index.</summary>
    public string SemanticConfigurationName { get; set; } = "retail-pulse-semantic";

    /// <summary>Vector search profile name declared on the index.</summary>
    public string VectorProfileName { get; set; } = "retail-pulse-vector";

    /// <summary>HNSW algorithm configuration name declared on the index.</summary>
    public string HnswAlgorithmName { get; set; } = "retail-pulse-hnsw";

    /// <summary>Embedding subsection binding.</summary>
    public AzureAISearchEmbeddingsOptions Embeddings { get; set; } = new();

    /// <summary>
    /// Blank <see cref="Endpoint"/> means the provider is not configured. The
    /// registration extension short-circuits without materializing any client,
    /// credential, or HTTP handler.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    /// Fails fast with an actionable message when a required field is missing
    /// on the enabled path. Called only after <see cref="IsConfigured"/> returned
    /// true — the disabled path never runs this.
    /// </summary>
    public void ValidateEnabled()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Endpoint is required to enable the Azure AI Search knowledge provider.");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Endpoint '{Endpoint}' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(IndexName))
        {
            throw new InvalidOperationException($"{SectionName}:IndexName is required.");
        }

        Embeddings.ValidateEnabled();
    }
}

/// <summary>
/// Embedding generation settings. Embeddings MUST be routed through the APIM
/// AI Gateway so tokens are metered, rate-limited, and audited alongside chat
/// completions.
/// </summary>
public sealed class AzureAISearchEmbeddingsOptions
{
    /// <summary>
    /// APIM inference endpoint used to call the embedding model. Reuses the
    /// gateway that already fronts chat completions — the same subscription
    /// key, backend policy, and diagnostics apply.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI deployment name for the embedding model.</summary>
    public string Deployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Vector dimensions produced by the embedding model. Must match the
    /// <c>contentVector</c> field on the index. Changing this requires a
    /// schema-version bump and a reindex.
    /// </summary>
    [Range(64, 4096)]
    public int Dimensions { get; set; } = 1536;

    /// <summary>Azure OpenAI REST api-version query parameter.</summary>
    public string ApiVersion { get; set; } = "2024-06-01";

    /// <summary>
    /// Cost-tracking model identifier fed into <see cref="Contracts.Observability.UsageEvent.Model"/>
    /// so per-model pricing in <see cref="Observability.TokenPricing"/> applies.
    /// Defaults to the deployment name when blank.
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Per-request timeout applied to the embeddings HTTP call (milliseconds).</summary>
    [Range(500, 120_000)]
    public int TimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Optional APIM subscription key. Preferred: managed identity via the
    /// gateway policy. When set, sent as the <c>api-key</c> header alongside
    /// the MI bearer token if the operator opts into hybrid auth for local
    /// testing.
    /// </summary>
    public string? ApimSubscriptionKey { get; set; }

    /// <summary>
    /// Whether to use managed identity for the embeddings call. Default true —
    /// the deployment contract mandates MI-only for hosted environments.
    /// </summary>
    public bool UseManagedIdentity { get; set; } = true;

    /// <summary>Agent identifier stamped on cost events raised for embeddings.</summary>
    public string CostTrackingAgentId { get; set; } = "azure-ai-search:embeddings";

    /// <summary>Resolves the model identifier used for cost tracking.</summary>
    public string ResolveModelId() =>
        string.IsNullOrWhiteSpace(ModelId) ? Deployment : ModelId;

    internal void ValidateEnabled()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException(
                $"{AzureAISearchOptions.SectionName}:Embeddings:Endpoint is required to enable the Azure AI Search knowledge provider — embeddings must traverse the APIM AI Gateway.");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{AzureAISearchOptions.SectionName}:Embeddings:Endpoint '{Endpoint}' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(Deployment))
        {
            throw new InvalidOperationException(
                $"{AzureAISearchOptions.SectionName}:Embeddings:Deployment is required.");
        }
    }
}
