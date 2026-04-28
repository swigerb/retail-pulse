using Azure.AI.Agents.Persistent;
using Azure.AI.Projects;
using Azure.Identity;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Extension methods for registering Foundry-hosted agents in the DI container.
/// </summary>
public static class AgentServiceExtensions
{
    /// <summary>
    /// Registers a typed agent provider that resolves a Foundry-hosted agent
    /// by friendly name and exposes it via <see cref="IAgentProvider{TAgent}"/>.
    /// <para>
    /// Usage:
    /// <code>
    /// builder.Services.AddAzureAgent&lt;IDistributionAnalysisAgent&gt;(opts =&gt;
    /// {
    ///     opts.FriendlyName = "Distribution Analysis Specialist";
    ///     opts.ProjectEndpoint = "https://...";
    /// });
    /// </code>
    /// </para>
    /// </summary>
    public static IServiceCollection AddAzureAgent<TAgent>(
        this IServiceCollection services,
        Action<AgentResolutionOptions> configure)
        where TAgent : class
    {
        var options = new AgentResolutionOptions
        {
            FriendlyName = "",
            ProjectEndpoint = ""
        };
        configure(options);

        if (string.IsNullOrWhiteSpace(options.FriendlyName))
            throw new ArgumentException("AgentResolutionOptions.FriendlyName is required.");
        if (string.IsNullOrWhiteSpace(options.ProjectEndpoint))
            throw new ArgumentException("AgentResolutionOptions.ProjectEndpoint is required.");

        // Create the AIProjectClient and PersistentAgentsClient once
        var projectClient = new AIProjectClient(
            new Uri(options.ProjectEndpoint),
            new DefaultAzureCredential());

        var persistentAgentsClient = projectClient.GetPersistentAgentsClient();

        // Register the provider as a singleton (resolution is lazy + cached)
        services.AddSingleton<IAgentProvider<TAgent>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<PersistentAgentProvider<TAgent>>>();
            return new PersistentAgentProvider<TAgent>(options, persistentAgentsClient, logger);
        });

        return services;
    }
}
