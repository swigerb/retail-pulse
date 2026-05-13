using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Models;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using Microsoft.Extensions.AI;

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
        Func<IServiceProvider, IEnumerable<AITool>>? competitiveToolsFactory = null)
    {
        // Register GeneralAgent as ISpecialistAgent
        services.AddScoped<GeneralAgent>(sp =>
        {
            var chatClient = sp.GetRequiredService<IChatClient>();
            var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
            var tools = toolsFactory(sp);
            var logger = sp.GetRequiredService<ILogger<GeneralAgent>>();
            var configuration = sp.GetRequiredService<IConfiguration>();

            return new GeneralAgent(chatClient, generalAgentDef, hubContext, tools, logger, configuration);
        });
        services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<GeneralAgent>());

        // Register DemandForecastAgent as ISpecialistAgent
        if (demandForecastDef is not null && demandToolsFactory is not null)
        {
            services.AddScoped<DemandForecastAgent>(sp =>
            {
                var chatClient = sp.GetRequiredService<IChatClient>();
                var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
                var tools = demandToolsFactory(sp);
                var logger = sp.GetRequiredService<ILogger<DemandForecastAgent>>();
                var configuration = sp.GetRequiredService<IConfiguration>();

                return new DemandForecastAgent(chatClient, demandForecastDef, hubContext, tools, logger, configuration);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<DemandForecastAgent>());
        }

        // Register PromoPlanningAgent as ISpecialistAgent
        if (promoPlanningDef is not null && promoToolsFactory is not null)
        {
            services.AddScoped<PromoPlanningAgent>(sp =>
            {
                var chatClient = sp.GetRequiredService<IChatClient>();
                var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
                var tools = promoToolsFactory(sp);
                var logger = sp.GetRequiredService<ILogger<PromoPlanningAgent>>();
                var configuration = sp.GetRequiredService<IConfiguration>();
                var approvalGate = sp.GetService<RetailPulse.Contracts.Approval.IApprovalGate>();

                return new PromoPlanningAgent(chatClient, promoPlanningDef, hubContext, tools, logger, configuration, approvalGate);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<PromoPlanningAgent>());
        }

        // Register CompetitiveIntelAgent as ISpecialistAgent
        if (competitiveIntelDef is not null && competitiveToolsFactory is not null)
        {
            services.AddScoped<CompetitiveIntelAgent>(sp =>
            {
                var chatClient = sp.GetRequiredService<IChatClient>();
                var hubContext = sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<Hubs.TelemetryHub>>();
                var tools = competitiveToolsFactory(sp);
                var logger = sp.GetRequiredService<ILogger<CompetitiveIntelAgent>>();
                var configuration = sp.GetRequiredService<IConfiguration>();
                var alertService = sp.GetService<Alerts.SqliteAlertService>();

                return new CompetitiveIntelAgent(chatClient, competitiveIntelDef, hubContext, tools, logger, configuration, alertService);
            });
            services.AddScoped<ISpecialistAgent>(sp => sp.GetRequiredService<CompetitiveIntelAgent>());
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
