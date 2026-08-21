using System.Collections.Concurrent;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Core;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Per-endpoint <see cref="PersistentAgentsClient"/> accessor owned by the
/// Foundry IQ knowledge provider. Today the accessor only sees the client the
/// provider itself lazily constructs via <see cref="GetOrCreate"/> — no other
/// feature (including <c>AgentServiceExtensions.AddAzureAgent&lt;TAgent&gt;</c>,
/// which constructs its own <see cref="PersistentAgentsClient"/> directly and
/// does not register one in DI) currently calls <see cref="Register"/>. If a
/// future integration wants to share a client across features that target the
/// same Foundry project, it should call <see cref="Register"/> before the
/// provider first resolves; the accessor's canonicalised-endpoint key is the
/// forward-compatible seam. See ADR-013 (Relationship with FoundryAgent:*).
///
/// The accessor is process-lifetime by design — <see cref="PersistentAgentsClient"/>
/// is thread-safe and the SDK owns its own HTTP pipeline.
/// </summary>
public sealed class FoundryClientAccessor
{
    private readonly ConcurrentDictionary<string, PersistentAgentsClient> _byEndpoint =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenCredential _credential;

    public FoundryClientAccessor(TokenCredential credential)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    /// <summary>
    /// Records an externally-constructed client for <paramref name="projectEndpoint"/>
    /// so a later <see cref="GetOrCreate"/> for the same endpoint returns it
    /// without building another. Idempotent — subsequent registrations for the
    /// same endpoint are ignored so the first-wins pattern matches the
    /// SDK singleton discipline. Returns the effective client for that endpoint.
    ///
    /// Currently no other Retail Pulse feature calls this — it is the wiring
    /// seam a future cross-surface sharing effort would use. See ADR-013.
    /// </summary>
    public PersistentAgentsClient Register(string projectEndpoint, PersistentAgentsClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectEndpoint);
        ArgumentNullException.ThrowIfNull(client);
        string key = Canonicalise(projectEndpoint);
        return _byEndpoint.GetOrAdd(key, _ => client);
    }

    /// <summary>
    /// Returns the shared client for <paramref name="projectEndpoint"/>,
    /// constructing it lazily via <see cref="AIProjectClient"/> when nothing
    /// has registered a client for that endpoint yet.
    /// </summary>
    public PersistentAgentsClient GetOrCreate(string projectEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectEndpoint);
        string key = Canonicalise(projectEndpoint);
        return _byEndpoint.GetOrAdd(key, endpoint =>
        {
            var projectClient = new AIProjectClient(new Uri(endpoint, UriKind.Absolute), _credential);
            return projectClient.GetPersistentAgentsClient();
        });
    }

    /// <summary>Diagnostic — number of endpoints currently keyed.</summary>
    public int EndpointCount => _byEndpoint.Count;

    private static string Canonicalise(string endpoint)
    {
        string trimmed = endpoint.Trim();
        return trimmed.EndsWith('/') ? trimmed[..^1] : trimmed;
    }
}
