using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Api.Telemetry;
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
    /// Registers the IAgentRouter and all specialist agents.
    /// Specialist agents are registered both individually (for keyed lookup)
    /// and as an IEnumerable&lt;ISpecialistAgent&gt; collection (for router discovery).
    /// </summary>
    public static IServiceCollection AddAgentRouting(
        this IServiceCollection services,
        PromptConfiguration promptConfig,
        AgentDefinition generalAgentDef,
        bool foundryEnabled,
        Func<IServiceProvider, IEnumerable<AITool>> toolsFactory,
        AgentDefinition? demandForecastDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? demandToolsFactory = null,
        AgentDefinition? promoPlanningDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? promoToolsFactory = null,
        AgentDefinition? competitiveIntelDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? competitiveToolsFactory = null,
        AgentDefinition? supplyChainDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? supplyToolsFactory = null,
        AgentDefinition? storeOpsDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? storeOpsToolsFactory = null,
        AgentDefinition? planogramDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? planogramToolsFactory = null,
        AgentDefinition? marginDef = null,
        Func<IServiceProvider, IEnumerable<AITool>>? marginToolsFactory = null)
    {
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
            return new AgentExecutionPipeline(chatClient, hubContext, streamingHubContext, streamingFeature, configuration, logger, metrics, anonymousChatPolicy);
        });

        // Register GeneralAgent as ISpecialistAgent
        services.AddScoped(sp =>
        {
            IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
            IEnumerable<AITool> tools = toolsFactory(sp);
            return new GeneralAgent(pipeline, generalAgentDef, tools);
        });
        services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<GeneralAgent>());

        // Register DemandForecastAgent as ISpecialistAgent
        if (demandForecastDef is not null && demandToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = demandToolsFactory(sp);
                return new DemandForecastAgent(pipeline, demandForecastDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<DemandForecastAgent>());
        }

        // Register PromoPlanningAgent as ISpecialistAgent
        if (promoPlanningDef is not null && promoToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = promoToolsFactory(sp);
                IApprovalGate? approvalGate = sp.GetService<IApprovalGate>();
                return new PromoPlanningAgent(pipeline, promoPlanningDef, tools, approvalGate);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PromoPlanningAgent>());
        }

        // Register CompetitiveIntelAgent as ISpecialistAgent
        if (competitiveIntelDef is not null && competitiveToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IHubContext<TelemetryHub> hubContext = sp.GetRequiredService<IHubContext<TelemetryHub>>();
                IEnumerable<AITool> tools = competitiveToolsFactory(sp);
                ILogger<CompetitiveIntelAgent> logger = sp.GetRequiredService<ILogger<CompetitiveIntelAgent>>();
                SqliteAlertService? alertService = sp.GetService<SqliteAlertService>();
                return new CompetitiveIntelAgent(pipeline, competitiveIntelDef, tools, hubContext, logger, alertService);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<CompetitiveIntelAgent>());
        }

        // Register SupplyChainAgent as ISpecialistAgent
        if (supplyChainDef is not null && supplyToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = supplyToolsFactory(sp);
                return new SupplyChainAgent(pipeline, supplyChainDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<SupplyChainAgent>());
        }

        // Register StoreOpsAgent as ISpecialistAgent
        if (storeOpsDef is not null && storeOpsToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = storeOpsToolsFactory(sp);
                return new StoreOpsAgent(pipeline, storeOpsDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<StoreOpsAgent>());
        }

        // Register PlanogramAgent as ISpecialistAgent
        if (planogramDef is not null && planogramToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = planogramToolsFactory(sp);
                return new PlanogramAgent(pipeline, planogramDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PlanogramAgent>());
        }

        // Register MarginAgent as ISpecialistAgent
        if (marginDef is not null && marginToolsFactory is not null)
        {
            services.AddScoped(sp =>
            {
                IAgentExecutionPipeline pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                IEnumerable<AITool> tools = marginToolsFactory(sp);
                return new MarginAgent(pipeline, marginDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MarginAgent>());
        }

        // Register MemoryManagementAgent as ISpecialistAgent
        services.AddScoped(sp =>
        {
            IConversationMemory memory = sp.GetRequiredService<IConversationMemory>();
            ILogger<MemoryManagementAgent> logger = sp.GetRequiredService<ILogger<MemoryManagementAgent>>();
            return new MemoryManagementAgent(memory, logger);
        });
        services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MemoryManagementAgent>());

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

            return new RetailOpsRouter(chatClient, routerDef, specialists, logger, classificationCache: cache);
        });

        return services;
    }
}
