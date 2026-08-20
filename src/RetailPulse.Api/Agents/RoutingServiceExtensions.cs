using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Api.Telemetry;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Extension methods for registering the multi-agent routing pipeline in DI.
/// </summary>
public static class RoutingServiceExtensions
{
    /// <summary>
    /// Registers the shared execution pipeline, the specialist roster (built from a
    /// <see cref="PromptConfiguration"/> plus a named <see cref="AgentToolRegistry"/>),
    /// and the <see cref="IAgentRouter"/> whose keyword fast-paths derive from configuration.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="promptConfig">Loaded prompts.yaml (already tenant-hydrated).</param>
    /// <param name="toolRegistry">Named tool registry — unknown names fail fast at startup.</param>
    /// <param name="orchestrationIntents">Intent config for orchestrators that are not
    /// specialists (Portfolio Health Council, Scorecard, etc.).</param>
    public static IServiceCollection AddAgentRouting(
        this IServiceCollection services,
        PromptConfiguration promptConfig,
        AgentToolRegistry toolRegistry,
        IReadOnlyList<RouterIntentConfig> orchestrationIntents)
    {
        ArgumentNullException.ThrowIfNull(promptConfig);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(orchestrationIntents);

        // Fail loudly at startup when a specialist references a tool the process
        // does not know about (issue #98 acceptance criterion). Includes every
        // configured agent def, so a typo in prompts.yaml never reaches runtime.
        toolRegistry.ValidateAllReferences(
            promptConfig.Agents.Values
                .Where(IsSpecialistDefinition)
                .Select(d => new AgentDefinitionRef(
                    Key: string.IsNullOrWhiteSpace(d.Key) ? d.Name : d.Key,
                    Tools: d.Tools)));

        // Detect duplicate specialist keys (case-insensitive) — the AgentDefinition
        // section name is a good default but explicit `key:` overrides can collide.
        var duplicateKeys = promptConfig.Agents.Values
            .Where(IsSpecialistDefinition)
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Key) ? d.Name : d.Key,
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate specialist agent key(s) in prompts.yaml: {string.Join(", ", duplicateKeys)}. " +
                "Each specialist must have a unique 'key' (or unique yaml section name when 'key' is omitted).");
        }

        services.AddSingleton(toolRegistry);
        services.AddSingleton(new RouterAgentRoster(promptConfig, orchestrationIntents));

        // Register the scoped streaming progress feature
        services.AddScoped<StreamingProgressFeature>();

        // Guarantee an IAnonymousChatPolicy is always resolvable wherever the pipeline is composed.
        // Anonymous mode registers the real AnonymousChatPolicy earlier (see
        // ProviderNeutralAuthentication.AddAnonymousMode), so this TryAdd is a no-op there and the
        // constrained policy wins. In Entra/Development the provider-neutral no-op is used. This
        // makes the pipeline's non-optional policy dependency satisfiable in every composition —
        // an omitted policy is a startup resolution failure, never a silent null runtime bypass.
        services.TryAddSingleton<IAnonymousChatPolicy, NoOpAnonymousChatPolicy>();

        // Register the shared execution pipeline
        services.AddScoped<IAgentExecutionPipeline>(sp =>
        {
            IChatClient chatClient = sp.GetRequiredService<IChatClient>();
            IHubContext<TelemetryHub> hubContext = sp.GetRequiredService<IHubContext<TelemetryHub>>();
            IHubContext<StreamingHub> streamingHubContext = sp.GetRequiredService<IHubContext<StreamingHub>>();
            StreamingProgressFeature streamingFeature = sp.GetRequiredService<StreamingProgressFeature>();
            IConfiguration configuration = sp.GetRequiredService<IConfiguration>();
            ILogger<AgentExecutionPipeline> logger = sp.GetRequiredService<ILogger<AgentExecutionPipeline>>();
            RetailPulseMetrics? metrics = sp.GetService<RetailPulseMetrics>();
            IAnonymousChatPolicy anonymousChatPolicy = sp.GetRequiredService<IAnonymousChatPolicy>();
            Budget.ToolResultBudget? toolBudget = sp.GetService<Budget.ToolResultBudget>();
            Budget.ToolResultBudgetOptions? budgetOptions = sp.GetService<Budget.ToolResultBudgetOptions>();
            TenantConfiguration tenant = sp.GetRequiredService<TenantConfiguration>();
            return new AgentExecutionPipeline(chatClient, hubContext, streamingHubContext, streamingFeature, configuration, logger, metrics, anonymousChatPolicy, tenant, toolBudget, budgetOptions);
        });

        // Register each specialist defined in configuration. Bespoke agents (Memory,
        // Competitive Intel) route to their hand-written classes by convention on
        // AgentDefinition.Key; every other agent is instantiated as the generic
        // ConfiguredSpecialistAgent — no C# changes required to add another.
        foreach (AgentDefinition specialistDef in promptConfig.Agents.Values.Where(IsSpecialistDefinition))
        {
            RegisterSpecialist(services, specialistDef);
        }

        // Register IAgentRouter → RetailOpsRouter
        AgentDefinition routerDef = promptConfig.Agents.TryGetValue("router", out AgentDefinition? def)
            ? def
            : throw new InvalidOperationException(
                "Missing 'router' agent definition in prompts.yaml. " +
                "Add an agents.router section with intent classification instructions.");

        services.AddScoped<IAgentRouter>(sp =>
        {
            IChatClient chatClient = sp.GetRequiredKeyedService<IChatClient>("router");
            IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
            ILogger<RetailOpsRouter> logger = sp.GetRequiredService<ILogger<RetailOpsRouter>>();
            RouterClassificationCache? cache = sp.GetService<RouterClassificationCache>();

            return new RetailOpsRouter(
                chatClient,
                routerDef,
                specialists,
                logger,
                intentConfigs: orchestrationIntents,
                classificationCache: cache);
        });

        return services;
    }

    /// <summary>
    /// True when an <see cref="AgentDefinition"/> represents a specialist agent that
    /// participates in routing. Router/synthesizer/vote-format prompts are
    /// orchestration entries — they still live in prompts.yaml but don't register
    /// as specialists. Detection is intentionally permissive: any def with a
    /// non-empty <c>Intents</c> list OR that carries the "specialist" role AND is
    /// not a well-known orchestration section.
    /// </summary>
    private static bool IsSpecialistDefinition(AgentDefinition def)
    {
        if (_orchestrationKeys.Contains(string.IsNullOrWhiteSpace(def.Key) ? def.Name : def.Key))
            return false;
        if (string.Equals(def.Role, "orchestration", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(def.Role, "router", StringComparison.OrdinalIgnoreCase))
            return false;

        // Fallback: legacy YAML without Role — treat any def with at least one
        // declared intent as a specialist. The retail-pulse "legacy general" doc
        // and every specialist covered by the pre-refactor code path do this.
        return def.Intents.Count > 0
            || string.Equals(def.Role, "specialist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Well-known composition-only section names that must never be routed to.</summary>
    private static readonly HashSet<string> _orchestrationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "router",
        "council-synthesis",
        "council-vote",
        "scorecard-synthesis",
        "exec-brief",
        "retail-pulse", // legacy composite prompt — not a specialist
        "planner", // #93: plan-first orchestration prompt — not a specialist
    };

    /// <summary>
    /// Registers a single specialist. Bespoke agents (Memory Management, Competitive
    /// Intel) get their hand-written class; everything else is a
    /// <see cref="ConfiguredSpecialistAgent"/>. Behaviour parity vs the pre-refactor
    /// hardcoded roster is preserved for every existing key.
    /// </summary>
    private static void RegisterSpecialist(IServiceCollection services, AgentDefinition def)
    {
        string key = string.IsNullOrWhiteSpace(def.Key) ? def.Name : def.Key;
        def.Key = key;

        switch (key.ToLowerInvariant())
        {
            case "memory-management":
                services.AddScoped(sp => new MemoryManagementAgent(
                    sp.GetRequiredService<IConversationMemory>(),
                    sp.GetRequiredService<ILogger<MemoryManagementAgent>>(),
                    def));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MemoryManagementAgent>());
                return;

            case "competitive-intel":
                services.AddScoped(sp => new CompetitiveIntelAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def),
                    sp.GetRequiredService<IHubContext<TelemetryHub>>(),
                    sp.GetRequiredService<ILogger<CompetitiveIntelAgent>>(),
                    sp.GetService<SqliteAlertService>()));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<CompetitiveIntelAgent>());
                return;

            case "general":
                services.AddScoped(sp => new GeneralAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<GeneralAgent>());
                return;

            case "demand-forecasting":
                services.AddScoped(sp => new DemandForecastAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<DemandForecastAgent>());
                return;

            case "promo-planning":
                services.AddScoped(sp => new PromoPlanningAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def),
                    sp.GetService<IApprovalGate>()));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PromoPlanningAgent>());
                return;

            case "field-sentiment":
                services.AddScoped(sp => new FieldSentimentAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<FieldSentimentAgent>());
                return;

            case "supply-chain":
                services.AddScoped(sp => new SupplyChainAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<SupplyChainAgent>());
                return;

            case "store-ops":
                services.AddScoped(sp => new StoreOpsAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<StoreOpsAgent>());
                return;

            case "planogram":
                services.AddScoped(sp => new PlanogramAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PlanogramAgent>());
                return;

            case "margin-analysis":
                services.AddScoped(sp => new MarginAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MarginAgent>());
                return;

            default:
                // Purely configuration-driven specialist. This is the "add-a-specialist-by-editing-yaml"
                // path (issue #98 acceptance criterion): no C# case is required to register it.
                services.AddScoped<ISpecialistAgent>(sp => new ConfiguredSpecialistAgent(
                    sp.GetRequiredService<IAgentExecutionPipeline>(),
                    def,
                    ResolveTools(sp, def)));
                return;
        }
    }

    private static IReadOnlyList<AITool> ResolveTools(IServiceProvider sp, AgentDefinition def)
    {
        AgentToolRegistry registry = sp.GetRequiredService<AgentToolRegistry>();
        return registry.Resolve(def.Tools, sp);
    }
}

/// <summary>
/// Composition-root roster of every configured agent definition plus the router
/// orchestration intent metadata. Exposed as a singleton so orchestrators can
/// derive council/scorecard/participant lists from configuration.
/// </summary>
public sealed class RouterAgentRoster
{
    public PromptConfiguration Prompts { get; }
    public IReadOnlyList<RouterIntentConfig> OrchestrationIntents { get; }

    public RouterAgentRoster(PromptConfiguration prompts, IReadOnlyList<RouterIntentConfig> orchestrationIntents)
    {
        Prompts = prompts;
        OrchestrationIntents = orchestrationIntents;
    }

    /// <summary>Agent keys with <c>council_participant: true</c>.</summary>
    public IReadOnlyList<string> GetCouncilParticipants() =>
        [.. Prompts.Agents.Values
            .Where(a => a.CouncilParticipant)
            .Select(a => string.IsNullOrWhiteSpace(a.Key) ? a.Name : a.Key)];

    /// <summary>Scorecard dimensions declared in configuration, ordered by weight desc.</summary>
    public IReadOnlyList<Scorecard.ScorecardDimensionConfig> GetScorecardDimensions() =>
        [.. Prompts.Agents.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.ScorecardDimension) && a.ScorecardWeight > 0)
            .Select(a => new Scorecard.ScorecardDimensionConfig(
                a.ScorecardDimension,
                a.ScorecardWeight,
                string.IsNullOrWhiteSpace(a.Key) ? a.Name : a.Key))
            .OrderByDescending(d => d.Weight)];
}
