using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Tools;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Api.Scorecard;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Acceptance-criteria coverage for issue #98 (data-driven agent definitions).
///
/// The proof test: a specialist declared purely through <see cref="PromptConfiguration"/>
/// — no new C# class, no DI wiring changes — must be routed to and executed.
/// The remaining tests exercise the startup validators (unknown tool, duplicate key,
/// missing router), the config-driven council/scorecard rosters, and the
/// unroutable-intent fallback to General.
/// </summary>
public sealed class DataDrivenSpecialistTests
{
    /// <summary>
    /// The proof: a specialist declared purely in configuration is registered as a
    /// <see cref="ConfiguredSpecialistAgent"/>, appears in the router's known intent
    /// set, receives a routed request via its keyword fast-path, and executes end-to-end
    /// through the shared pipeline. No C# class, no DI branch, no rebuild — the objective
    /// of ADR-008.
    /// </summary>
    [Fact]
    public async Task TestOnlySpecialist_AddedThroughConfigurationOnly_IsRoutedAndExecuted()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: true);

        Mock<IChatClient> chatClient = new();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant,
                /*lang=json,strict*/ "{\"intent\":\"general/fallback\",\"confidence\":0.5}")));

        // Stub the ConfiguredSpecialistAgent pipeline so we can observe the route
        // without wiring the full MAF stack — the test only proves the routing path
        // reaches the configured specialist.
        Mock<IAgentExecutionPipeline> pipelineMock = new();
        pipelineMock
            .Setup(p => p.ExecuteAsync(
                It.IsAny<AgentExecutionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentExecutionContext ctx, CancellationToken _) =>
                new ChatResponse(
                    Reply: $"handled by {ctx.AgentName}: {ctx.Request.Message}",
                    SessionId: ctx.Request.SessionId ?? "s",
                    Spans: []));

        AgentToolRegistry toolRegistry = new();

        ServiceCollection services = new();
        SeedBaselineServices(services, chatClient.Object);

        services.AddAgentRouting(promptConfig, toolRegistry, OrchestrationIntents());

        // Override the pipeline registration added by AddAgentRouting so we can observe
        // the specialist invocation without a live LLM. Last-wins for GetRequiredService.
        services.AddScoped(_ => pipelineMock.Object);

        ServiceProvider sp = services.BuildServiceProvider();

        IAgentRouter router = sp.GetRequiredService<IAgentRouter>();
        var concreteRouter = (RetailOpsRouter)router;

        concreteRouter.KnownIntents.Should().Contain("test/widget",
            "a pure-config specialist's intent must appear in the router's known intent set");

        RoutingDecision decision = await router.RouteAsync(
            "please spin up the widget flux capacitor now", null, null, null);

        decision.AgentKey.Should().Be("test-widget",
            "the widget keyword fast-path should route to the test-only specialist");
        decision.Intent.Should().Be("test/widget");

        IEnumerable<ISpecialistAgent> specialists = sp.GetServices<ISpecialistAgent>();
        ISpecialistAgent widget = specialists.Single(s => s.Key == "test-widget");
        widget.Should().BeOfType<Api.Agents.Specialists.ConfiguredSpecialistAgent>();

        ChatResponse response = await widget.HandleAsync(
            new ChatRequest("spin up the widget flux capacitor", SessionId: "s"));

        response.Reply.Should().Contain("handled by Widget Specialist");
    }

    /// <summary>Config errors must fail at startup, never at first user query.</summary>
    [Fact]
    public void UnknownToolReference_FailsFastAtStartup_WithActionableMessage()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);
        promptConfig.Agents["demand-forecast"].Tools = ["GetDepletion", "ThisToolDoesNotExist"];

        AgentToolRegistry toolRegistry = new();
        toolRegistry.Register("GetDepletion", _ => new StubTool());
        toolRegistry.Register("CreateChart", _ => new StubTool());

        ServiceCollection services = new();

        Action wire = () => services.AddAgentRouting(promptConfig, toolRegistry, OrchestrationIntents());

        UnknownToolReferenceException ex = wire.Should().Throw<UnknownToolReferenceException>()
            .Which;

        ex.MissingTools.Should().Contain("ThisToolDoesNotExist");
        ex.Message.Should().Contain("Fix the 'tools:' entry in prompts.yaml");
    }

    [Fact]
    public void DuplicateSpecialistKey_FailsFastAtStartup()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);

        // Two definitions share the same effective Key.
        promptConfig.Agents["another-general"] = new AgentDefinition
        {
            Name = "Another General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "duplicate.",
            Temperature = 0.3,
            Key = "general",
            Intents = ["general/fallback"],
        };

        AgentToolRegistry toolRegistry = MinimalToolRegistry();

        ServiceCollection services = new();
        Action wire = () => services.AddAgentRouting(promptConfig, toolRegistry, OrchestrationIntents());

        wire.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate specialist agent key*general*");
    }

    [Fact]
    public void MissingRouterDefinition_FailsFastAtStartup()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);
        promptConfig.Agents.Remove("router");

        AgentToolRegistry toolRegistry = MinimalToolRegistry();

        ServiceCollection services = new();
        SeedBaselineServices(services, Mock.Of<IChatClient>());

        Action wire = () => services.AddAgentRouting(promptConfig, toolRegistry, OrchestrationIntents());
        wire.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing 'router' agent definition*");
    }

    [Fact]
    public void CouncilParticipants_DeriveFromConfiguration()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);
        RouterAgentRoster roster = new(promptConfig, OrchestrationIntents());

        roster.GetCouncilParticipants().Should().BeEquivalentTo(
            ["demand-forecasting", "supply-chain"],
            "only agents with council_participant: true should join the council fan-out");
    }

    [Fact]
    public void ScorecardDimensions_DeriveFromConfiguration_OrderedByWeight()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);
        RouterAgentRoster roster = new(promptConfig, OrchestrationIntents());

        IReadOnlyList<ScorecardDimensionConfig> dims = roster.GetScorecardDimensions();

        dims.Should().HaveCount(2, "two specialists declare scorecard dimensions in this fixture");
        dims[0].Dimension.Should().Be("Demand Momentum");
        dims[0].Weight.Should().Be(0.25);
        dims[1].Dimension.Should().Be("Supply Reliability");
        dims[1].Weight.Should().Be(0.20);
    }

    [Fact]
    public async Task UnroutableIntent_FallsBackToGeneral()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: false);

        // LLM returns an intent that no specialist claims.
        Mock<IChatClient> chatClient = new();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant,
                /*lang=json,strict*/ "{\"intent\":\"totally/unknown\",\"confidence\":0.9}")));

        ServiceCollection services = new();
        SeedBaselineServices(services, chatClient.Object);

        services.AddAgentRouting(promptConfig, MinimalToolRegistry(), OrchestrationIntents());
        using ServiceProvider sp = services.BuildServiceProvider();

        IAgentRouter router = sp.GetRequiredService<IAgentRouter>();
        RoutingDecision decision = await router.RouteAsync("anything at all", null, null, null);

        decision.AgentKey.Should().Be("general",
            "unroutable intents must degrade to the General agent, never crash");
        decision.Intent.Should().Be(AgentIntent.General);
    }

    [Fact]
    public void KnownIntents_Include_EveryConfiguredSpecialistIntent()
    {
        PromptConfiguration promptConfig = BuildPromptConfig(includeTestWidget: true);

        ServiceCollection services = new();
        SeedBaselineServices(services, Mock.Of<IChatClient>());

        services.AddAgentRouting(promptConfig, MinimalToolRegistry(), OrchestrationIntents());
        using ServiceProvider sp = services.BuildServiceProvider();

        var router = (RetailOpsRouter)sp.GetRequiredService<IAgentRouter>();

        router.KnownIntents.Should().Contain("demand/forecasting");
        router.KnownIntents.Should().Contain("supply/shipments");
        router.KnownIntents.Should().Contain("test/widget");
        router.KnownIntents.Should().Contain("council/health",
            "orchestration intents supplied to AddAgentRouting must also be advertised");
    }

    /// <summary>
    /// Wire up the DI baseline that <see cref="RoutingServiceExtensions.AddAgentRouting"/>
    /// needs to resolve (IChatClient, IHubContext stubs, IConfiguration, tenant, logging).
    /// Kept in one place so every test uses the same minimal composition.
    /// </summary>
    private static void SeedBaselineServices(IServiceCollection services, IChatClient chatClient)
    {
        services.AddLogging();
        services.AddSingleton(chatClient);
        services.AddKeyedSingleton("router", chatClient);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(new TenantConfiguration
        {
            Company = "Test Co",
            Industry = "Test",
        });
        services.AddSingleton(Mock.Of<IHubContext<TelemetryHub>>());
        services.AddSingleton(Mock.Of<IHubContext<StreamingHub>>());
    }

    // Minimal-but-realistic fixture: router + general + two council specialists + optional test widget.
    private static PromptConfiguration BuildPromptConfig(bool includeTestWidget)
    {
        PromptConfiguration cfg = new();

        cfg.Agents["router"] = new AgentDefinition
        {
            Name = "Router",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Classify intent.",
            Temperature = 0.0,
            Role = "orchestration",
            Key = "router",
        };

        cfg.Agents["general"] = new AgentDefinition
        {
            Name = "General",
            Model = "gpt-5.4-mini",
            SystemPrompt = "General fallback.",
            Temperature = 0.3,
            Key = "general",
            Intents = [AgentIntent.General],
        };

        cfg.Agents["demand-forecast"] = new AgentDefinition
        {
            Name = "Demand Forecast",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Forecast demand.",
            Temperature = 0.2,
            Key = "demand-forecasting",
            Intents = [AgentIntent.DemandForecasting],
            KeywordFastPaths = ["demand forecast"],
            CouncilParticipant = true,
            ScorecardDimension = "Demand Momentum",
            ScorecardWeight = 0.25,
        };

        cfg.Agents["supply-chain"] = new AgentDefinition
        {
            Name = "Supply Chain",
            Model = "gpt-5.4-mini",
            SystemPrompt = "Supply chain.",
            Temperature = 0.2,
            Key = "supply-chain",
            Intents = [AgentIntent.SupplyShipments],
            KeywordFastPaths = ["shipment status"],
            CouncilParticipant = true,
            ScorecardDimension = "Supply Reliability",
            ScorecardWeight = 0.20,
        };

        if (includeTestWidget)
        {
            cfg.Agents["test-widget"] = new AgentDefinition
            {
                Name = "Widget Specialist",
                Model = "gpt-5.4-mini",
                SystemPrompt = "You handle widget flux capacitor requests.",
                Temperature = 0.1,
                Key = "test-widget",
                Intents = ["test/widget"],
                KeywordFastPaths = ["widget flux capacitor"],
                FallbackReply = "widget standby",
            };
        }

        return cfg;
    }

    private static AgentToolRegistry MinimalToolRegistry()
    {
        AgentToolRegistry registry = new();
        registry.Register("CreateChart", _ => new StubTool());
        return registry;
    }

    private static IReadOnlyList<RouterIntentConfig> OrchestrationIntents() =>
    [
        new(AgentIntent.PortfolioHealth, ["portfolio health"]),
        new(AgentIntent.Scorecard, ["scorecard"]),
    ];

    /// <summary>Placeholder AITool for tool-registry wiring — never invoked in these tests.</summary>
    private sealed class StubTool : AITool
    {
        public override string Name => "stub";
    }
}
