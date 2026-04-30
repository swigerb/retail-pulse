using Azure.AI.Agents.Persistent;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Configuration options for agent resolution.
/// </summary>
public sealed class AgentResolutionOptions
{
    /// <summary>
    /// Human-readable agent name used for discovery
    /// (e.g., "Distribution Analysis Specialist").
    /// </summary>
    public required string FriendlyName { get; set; }

    /// <summary>
    /// Azure AI Foundry project endpoint URL.
    /// </summary>
    public required string ProjectEndpoint { get; set; }

    /// <summary>
    /// Optional direct agent ID (asst_* format) for emergency bypass.
    /// If set, skips name-based resolution entirely.
    /// </summary>
    public string? DirectAgentId { get; set; }
}

/// <summary>
/// Resolves a Foundry-hosted agent by friendly name via
/// <see cref="PersistentAgentsClient.Administration"/> and caches the
/// result. Thread-safe: concurrent callers share a single resolution attempt.
/// </summary>
public sealed class PersistentAgentProvider<TAgent> : IAgentProvider<TAgent>
    where TAgent : class
{
    private readonly AgentResolutionOptions _options;
    private readonly PersistentAgentsClient _client;
    private readonly ILogger<PersistentAgentProvider<TAgent>> _logger;

    private AgentInfo? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PersistentAgentProvider(
        AgentResolutionOptions options,
        PersistentAgentsClient client,
        ILogger<PersistentAgentProvider<TAgent>> logger)
    {
        _options = options;
        _client = client;
        _logger = logger;
    }

    public PersistentAgentsClient GetAgentsClient() => _client;

    public async Task<string> GetAgentIdAsync(CancellationToken ct = default)
        => (await GetAgentInfoAsync(ct)).Id;

    public async Task<AgentInfo> GetAgentInfoAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cached is not null) return _cached;

            _cached = await ResolveAsync(ct);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<AgentInfo> ResolveAsync(CancellationToken ct)
    {
        // Fast path: direct ID configured (emergency bypass)
        if (!string.IsNullOrEmpty(_options.DirectAgentId))
        {
            _logger.LogInformation(
                "Using direct agent ID '{AgentId}' for {AgentType} (bypassing name resolution)",
                _options.DirectAgentId, typeof(TAgent).Name);

            return Task.FromResult(new AgentInfo(
                _options.DirectAgentId, _options.FriendlyName, "Azure AI Foundry"));
        }

        // Name-based resolution: list all persistent agents, match by name
        string? resolvedId = null;
        var agents = _client.Administration.GetAgents(cancellationToken: ct);
        foreach (var agent in agents)
        {
            if (string.Equals(agent.Name, _options.FriendlyName, StringComparison.OrdinalIgnoreCase))
            {
                resolvedId = agent.Id;
                // Don't break — take the latest if there are duplicates
            }
        }

        if (string.IsNullOrEmpty(resolvedId))
        {
            throw new InvalidOperationException(
                $"Foundry agent named '{_options.FriendlyName}' not found. " +
                $"Ensure the agent exists in project '{_options.ProjectEndpoint}' " +
                $"or configure a direct agent ID via 'FoundryAgent:ShipmentAgentId'.");
        }

        _logger.LogInformation(
            "Resolved Foundry agent '{FriendlyName}' → ID: {AgentId} (type: {AgentType})",
            _options.FriendlyName, resolvedId, typeof(TAgent).Name);

        return Task.FromResult(new AgentInfo(
            resolvedId, _options.FriendlyName, "Azure AI Foundry"));
    }
}
