using Microsoft.Extensions.AI;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Models;
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

        // Register the shared execution pipeline
        services.AddScoped<IAgentExecutionPipeline>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
            var streamingHubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.StreamingHub>>();
            var streamingFeature = sp.GetRequiredService<StreamingProgressFeature>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetRequiredService<ILogger<AgentExecutionPipeline>>();
            var metrics = sp.GetService<Telemetry.RetailPulseMetrics>();
            return new AgentExecutionPipeline(chatClient, hubContext, streamingHubContext, streamingFeature, configuration, logger, metrics);
        });

        // Register GeneralAgent as ISpecialistAgent
        services.AddScoped<GeneralAgent>(sp =>
        {
            var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
            var tools = toolsFactory(sp);
            return new GeneralAgent(pipeline, generalAgentDef, tools);
        });
        services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<GeneralAgent>());

        // Register DemandForecastAgent as ISpecialistAgent
        if (demandForecastDef is not null && demandToolsFactory is not null)
        {
            services.AddScoped<DemandForecastAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = demandToolsFactory(sp);
                return new DemandForecastAgent(pipeline, demandForecastDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<DemandForecastAgent>());
        }

        // Register PromoPlanningAgent as ISpecialistAgent
        if (promoPlanningDef is not null && promoToolsFactory is not null)
        {
            services.AddScoped<PromoPlanningAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = promoToolsFactory(sp);
                var approvalGate = sp.GetService<RetailPulse.Contracts.Approval.IApprovalGate>();
                return new PromoPlanningAgent(pipeline, promoPlanningDef, tools, approvalGate);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PromoPlanningAgent>());
        }

        // Register CompetitiveIntelAgent as ISpecialistAgent
        if (competitiveIntelDef is not null && competitiveToolsFactory is not null)
        {
            services.AddScoped<CompetitiveIntelAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
                var tools = competitiveToolsFactory(sp);
                var logger = sp.GetRequiredService<ILogger<CompetitiveIntelAgent>>();
                var alertService = sp.GetService<Alerts.SqliteAlertService>();
                return new CompetitiveIntelAgent(pipeline, competitiveIntelDef, tools, hubContext, logger, alertService);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<CompetitiveIntelAgent>());
        }

        // Register SupplyChainAgent as ISpecialistAgent
        if (supplyChainDef is not null && supplyToolsFactory is not null)
        {
            services.AddScoped<SupplyChainAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = supplyToolsFactory(sp);
                return new SupplyChainAgent(pipeline, supplyChainDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<SupplyChainAgent>());
        }

        // Register StoreOpsAgent as ISpecialistAgent
        if (storeOpsDef is not null && storeOpsToolsFactory is not null)
        {
            services.AddScoped<StoreOpsAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = storeOpsToolsFactory(sp);
                return new StoreOpsAgent(pipeline, storeOpsDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<StoreOpsAgent>());
        }

        // Register PlanogramAgent as ISpecialistAgent
        if (planogramDef is not null && planogramToolsFactory is not null)
        {
            services.AddScoped<PlanogramAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = planogramToolsFactory(sp);
                return new PlanogramAgent(pipeline, planogramDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PlanogramAgent>());
        }

        // Register MarginAgent as ISpecialistAgent
        if (marginDef is not null && marginToolsFactory is not null)
        {
            services.AddScoped<MarginAgent>(sp =>
            {
                var pipeline = sp.GetRequiredService<IAgentExecutionPipeline>();
                var tools = marginToolsFactory(sp);
                return new MarginAgent(pipeline, marginDef, tools);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MarginAgent>());
        }

        // Register MemoryManagementAgent as ISpecialistAgent
        services.AddScoped<MemoryManagementAgent>(sp =>
        {
            var memory = sp.GetRequiredService<IConversationMemory>();
            var logger = sp.GetRequiredService<ILogger<MemoryManagementAgent>>();
            return new MemoryManagementAgent(memory, logger);
        });
        services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<MemoryManagementAgent>());

        // Register IAgentRouter → RetailOpsRouter
        var routerDef = promptConfig.Agents.TryGetValue("router", out var def)
            ? def
            : throw new InvalidOperationException(
                "Missing 'router' agent definition in prompts.yaml. " +
                "Add an agents.router section with intent classification instructions.");

        services.AddScoped<IAgentRouter>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var specialists = sp.GetServices<ISpecialistAgent>();
            var logger = sp.GetRequiredService<ILogger<RetailOpsRouter>>();

            return new RetailOpsRouter(chatClient, routerDef, specialists, logger);
        });

        return services;
    }
}
