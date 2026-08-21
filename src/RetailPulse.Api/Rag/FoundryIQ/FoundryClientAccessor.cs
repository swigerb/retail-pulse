using System.Collections.Concurrent;
using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Core;

namespace RetailPulse.Api.Rag.FoundryIQ;

/// <summary>
/// Shared, per-endpoint <see cref="PersistentAgentsClient"/> accessor used by
/// the Foundry IQ knowledge provider. When another consumer registers a
/// singleton <see cref="PersistentAgentsClient"/> (today only
/// <c>AgentServiceExtensions.AddAzureAgent&lt;TAgent&gt;</c>) that targets the
/// same endpoint, this accessor returns the shared instance instead of
/// constructing a duplicate. Different endpoints get separate clients keyed
/// by canonicalised endpoint URL.
///
/// The accessor is process-lifetime by design — <see cref="PersistentAgentsClient"/>
/// is thread-safe and the SDK owns its own HTTP pipeline. See ADR-013
/// (Relationship with FoundryAgent:*).
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
