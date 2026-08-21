using System.ComponentModel.DataAnnotations;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Configuration surface for the optional Foundry IQ (file_search) knowledge
/// provider (issue #104). Bound to the <c>Knowledge:FoundryIQ</c> section.
///
/// The provider is FULLY OPTIONAL. When <see cref="ProjectEndpoint"/> is blank
/// (or no vector-store selector is set) the provider is not registered and
/// no <c>PersistentAgentsClient</c>, <c>AIProjectClient</c>, credential, or
/// HTTP handler is materialized — the default demo path stays byte-for-byte
/// unchanged. See docs/adr/013-foundry-iq-knowledge-provider.md.
/// </summary>
public sealed class FoundryIQOptions
{
    /// <summary>Configuration section: <c>Knowledge:FoundryIQ</c>.</summary>
    public const string SectionName = "Knowledge:FoundryIQ";

    /// <summary>
    /// Fully-qualified Foundry project endpoint
    /// (e.g. <c>https://&lt;foundry&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>).
    /// Blank means the provider is disabled.
    /// </summary>
    public string? ProjectEndpoint { get; set; }

    /// <summary>
    /// Human-friendly vector store name resolved by paging
    /// <c>VectorStores.GetVectorStoresAsync</c>. Either this or
    /// <see cref="VectorStoreId"/> must be non-blank on the enabled path.
    /// </summary>
    public string? VectorStoreName { get; set; }

    /// <summary>
    /// Optional exact <c>vs_...</c> vector store id. When present, resolution
    /// bypasses the name lookup entirely (emergency bypass, same pattern as
    /// <c>AgentResolutionOptions.DirectAgentId</c>).
    /// </summary>
    public string? VectorStoreId { get; set; }

    /// <summary>
    /// Name of the internal retrieval agent Retail Pulse creates (get-or-create)
    /// to run file_search against the resolved vector store.
    /// </summary>
    public string RetrievalAgentName { get; set; } = "retail-pulse-foundry-iq-retrieval";

    /// <summary>
    /// Optional exact <c>asst_...</c> retrieval agent id. When present,
    /// resolution bypasses name-based get-or-create entirely.
    /// </summary>
    public string? RetrievalAgentId { get; set; }

    /// <summary>
    /// Foundry model deployment name used by the retrieval agent (e.g.
    /// <c>gpt-5.4-mini</c>). Required on the enabled path when a retrieval
    /// agent may need to be created — an existing agent id (via
    /// <see cref="RetrievalAgentId"/>) or an existing named agent bypasses
    /// this at runtime, but startup validation still requires the value so a
    /// misconfigured deployment fails fast rather than at the first search.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Per-search bounded timeout (milliseconds). Applied via a linked
    /// <see cref="CancellationTokenSource"/> around every SDK call chain so a
    /// stuck Foundry run can never hang an HTTP request indefinitely. Matches
    /// the FoundryShipmentAgent pattern.
    /// </summary>
    [Range(1_000, 300_000)]
    public int RequestTimeoutMs { get; set; } = 60_000;

    /// <summary>Polling interval for <c>GetRunAsync</c> while the run is queued/in-progress.</summary>
    [Range(50, 10_000)]
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>
    /// Ceiling on how many file_search results to request from Foundry per
    /// <c>SearchAsync</c>. Callers' <c>topK</c> is clamped to this so a
    /// runaway request cannot ask Foundry for a huge result set.
    /// </summary>
    [Range(1, 100)]
    public int MaxResults { get; set; } = 20;

    /// <summary>
    /// Agent identifier stamped on <c>UsageEvent</c>s raised on the shared
    /// <c>ICostTracker</c> for each completed retrieval run.
    /// </summary>
    public string CostTrackingAgentId { get; set; } = "foundry-iq:retrieval";

    /// <summary>
    /// Blank <see cref="ProjectEndpoint"/> or missing vector-store selector
    /// means the provider is not configured. The registration extension
    /// short-circuits without materializing any client, credential, or
    /// HTTP handler.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProjectEndpoint) &&
        (!string.IsNullOrWhiteSpace(VectorStoreName) || !string.IsNullOrWhiteSpace(VectorStoreId));

    /// <summary>
    /// Fails fast with an actionable message when a required field is missing
    /// on the enabled path. Called only after <see cref="IsConfigured"/>
    /// returned true — the disabled path never runs this.
    /// </summary>
    public void ValidateEnabled()
    {
        if (string.IsNullOrWhiteSpace(ProjectEndpoint))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProjectEndpoint is required to enable the Foundry IQ knowledge provider.");
        }

        if (!Uri.TryCreate(ProjectEndpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ProjectEndpoint '{ProjectEndpoint}' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(VectorStoreName) && string.IsNullOrWhiteSpace(VectorStoreId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:VectorStoreName or {SectionName}:VectorStoreId is required to bind the file_search retrieval agent.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Model is required — the Foundry retrieval agent needs a project deployment name.");
        }

        if (string.IsNullOrWhiteSpace(RetrievalAgentName) && string.IsNullOrWhiteSpace(RetrievalAgentId))
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetrievalAgentName or {SectionName}:RetrievalAgentId is required so the retrieval agent can be resolved deterministically.");
        }
    }
}
