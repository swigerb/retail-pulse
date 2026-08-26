using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Alerts;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Memory;
using RetailPulse.Api.Models;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Alerts;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Memory;
using RetailPulse.Contracts.Routing;
using RetailPulse.Contracts.Tracing;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Integration;

/// <summary>
/// Phase 1 regression suite: verifies ALL features work together.
/// Covers: routing, memory, approval, alerts, tracing, tools, full pipeline.
/// 15+ tests.
/// </summary>
public class Phase1IntegrationTests : IDisposable
{
    private readonly string _memoryDbPath;
    private readonly string _approvalDbPath;
    private readonly SqliteConversationMemory _memory;
    private readonly SqliteApprovalGate _approvalGate;
    private readonly InMemoryAlertService _alertService;
    private readonly InMemoryTraceCollector _traceCollector;

    public Phase1IntegrationTests()
    {
        _memoryDbPath = SqliteTestCleanup.NewDbPath("phase1_mem");
        _approvalDbPath = SqliteTestCleanup.NewDbPath("phase1_appr");
        _memory = new SqliteConversationMemory(_memoryDbPath, Mock.Of<ILogger<SqliteConversationMemory>>());
        _approvalGate = new SqliteApprovalGate(_approvalDbPath, Mock.Of<ILogger<SqliteApprovalGate>>());
        _alertService = new InMemoryAlertService(throttleWindow: TimeSpan.FromMilliseconds(50));
        _traceCollector = new InMemoryTraceCollector();
    }

    public void Dispose()
    {
        _memory.Dispose();
        CleanDb(_memoryDbPath);
        CleanDb(_approvalDbPath);
    }

    private static void CleanDb(string path)
    {
        SqliteTestCleanup.ReleaseAndDelete(path);
    }

    #region Router Still Routes Correctly

    [Fact]
    public async Task Router_DemandMessage_RoutesToDemandForecastAgent()
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.92,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");

        ISpecialistAgent demandAgent = CreateMockSpecialist("demand-forecasting", AgentIntent.DemandForecasting);
        GeneralAgent generalAgent = CreateGeneralAgent(MockChatClient("general fallback"));
        var specialists = new List<ISpecialistAgent> { demandAgent, generalAgent };
        RetailOpsRouter router = CreateRouter(routerClient, specialists);

        RoutingDecision result = await router.RouteAsync("What is the demand forecast for Brand X?", null, null, null);

        result.Intent.Should().Be(AgentIntent.DemandForecasting);
        result.AgentKey.Should().Be("demand-forecasting");
        result.Confidence.Should().BeGreaterThan(0.6);
    }

    [Fact]
    public async Task Router_GeneralMessage_RoutesToGeneralAgent()
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.General}\",\"confidence\":0.85,\"intents\":[\"{AgentIntent.General}\"]}}");

        GeneralAgent generalAgent = CreateGeneralAgent(MockChatClient("Here is the overview."));
        var specialists = new List<ISpecialistAgent> { generalAgent };
        RetailOpsRouter router = CreateRouter(routerClient, specialists);

        RoutingDecision result = await router.RouteAsync("Show me the portfolio overview", null, null, null);

        result.Intent.Should().Be(AgentIntent.General);
        result.AgentKey.Should().Be("general");
    }

    [Fact]
    public async Task Router_LowConfidence_FallsBackToGeneral()
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.SupplyShipments}\",\"confidence\":0.3,\"intents\":[\"{AgentIntent.SupplyShipments}\"]}}");

        GeneralAgent generalAgent = CreateGeneralAgent(MockChatClient("Can you be more specific?"));
        RetailOpsRouter router = CreateRouter(routerClient, [generalAgent]);

        RoutingDecision result = await router.RouteAsync("Tell me stuff", null, null, null);

        result.Intent.Should().Be(AgentIntent.General, "low confidence should fall back to general");
    }

    #endregion

    #region Memory Still Works

    [Fact]
    public async Task Memory_StoresAndRecallsAcrossTurns()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entry1 = new MemoryEntry(Guid.NewGuid().ToString("N"), "user-1",
            MemoryType.ConversationSummary, "Discussed Q4 demand trends",
            null, now, now.AddDays(30));
        var entry2 = new MemoryEntry(Guid.NewGuid().ToString("N"), "user-1",
            MemoryType.EntityMention, "Brand X sales analysis",
            "Brand X", now, now.AddDays(30));

        await _memory.StoreAsync("user-1", entry1);
        await _memory.StoreAsync("user-1", entry2);

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync("user-1", maxResults: 10);

        recalled.Should().HaveCount(2);
        recalled.Should().Contain(m => m.Content.Contains("Q4 demand"));
        recalled.Should().Contain(m => m.Content.Contains("Brand X"));
    }

    [Fact]
    public async Task Memory_ForgetEverything_ClearsAllEntries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _memory.StoreAsync("user-forget", new MemoryEntry(
            Guid.NewGuid().ToString("N"), "user-forget",
            MemoryType.ConversationSummary, "Some data",
            null, now, now.AddDays(30)));

        await _memory.ForgetAsync("user-forget");

        IReadOnlyList<MemoryEntry> recalled = await _memory.RecallAsync("user-forget");
        recalled.Should().BeEmpty("ForgetAsync should purge all entries");
    }

    #endregion

    #region Approval Gate Still Works

    [Fact]
    public async Task ApprovalGate_RequestRespondResult()
    {
        var context = new ApprovalContext("demand-agent", "user-1",
            "Generate Q4 forecast", "High compute", "Medium", "Quarterly forecast");

        ApprovalRequest request = await _approvalGate.RequestApprovalAsync(context);
        request.Decision.Should().Be(ApprovalDecision.Pending);

        await _approvalGate.RespondAsync(request.RequestId, ApprovalDecision.Approved, "OK to proceed");

        ApprovalResult result = await _approvalGate.GetResultAsync(request.RequestId);
        result.Decision.Should().Be(ApprovalDecision.Approved);
        result.Comment.Should().Be("OK to proceed");
    }

    #endregion

    #region DemandForecastAgent Tools Return Valid Data

    [Fact]
    public async Task DemandForecastAgent_HandleAsync_ReturnsValidResponse()
    {
        IChatClient chatClient = MockChatClient("Brand X demand is projected to grow 15% next quarter.");
        IHubContext<TelemetryHub> hubContext = CreateMockHubContext();
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pipeline = new AgentExecutionPipeline(
            chatClient, hubContext, config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
        var agent = new DemandForecastAgent(
            pipeline,
            new AgentDefinition { Name = "DemandForecast", Model = "gpt-5.4-mini", SystemPrompt = "Demand specialist", Temperature = 0.3 },
            []);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("Forecast for Brand X", SessionId: "demand-test"));

        response.Should().NotBeNull();
        response.Reply.Should().Contain("Brand X");
        response.SessionId.Should().Be("demand-test");
        response.Spans.Should().NotBeEmpty();
    }

    #endregion

    #region GeneralAgent Original Tools Still Work

    [Fact]
    public async Task GeneralAgent_ReturnsValidResponse()
    {
        IChatClient chatClient = MockChatClient("Here are the portfolio depletions for last month.");
        GeneralAgent agent = CreateGeneralAgent(chatClient);

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("Show me portfolio depletions", SessionId: "general-test"));

        response.Should().NotBeNull();
        response.Reply.Should().NotBeNullOrEmpty();
        response.Spans.Should().NotBeEmpty();
        response.Spans.Should().Contain(s => s.Type == "thought");
        response.Spans.Should().Contain(s => s.Type == "response");
    }

    [Fact]
    public async Task GeneralAgent_EmitsSpansWithSessionId()
    {
        GeneralAgent agent = CreateGeneralAgent(MockChatClient("done"));

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "span-check"));

        response.Spans.Should().OnlyContain(s => s.SessionId == "span-check");
    }

    [Fact]
    public async Task GeneralAgent_TotalDurationMs_Populated()
    {
        GeneralAgent agent = CreateGeneralAgent(MockChatClient("done"));

        Contracts.ChatResponse response = await agent.HandleAsync(
            new ChatRequest("test", SessionId: "dur-check"));

        response.TotalDurationMs.Should().NotBeNull();
        response.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Alert Service Detects Seeded Anomalies

    [Fact]
    public async Task AlertService_DetectsSeededAnomalies()
    {
        _alertService.SeedDataPoint("Brand A", "West", "demand_spike", baseline: 1000, current: 1500);
        _alertService.SeedDataPoint("Brand B", "East", "supply_drop", baseline: 1000, current: 700);

        IReadOnlyList<Alert> alerts = await _alertService.CheckForAlertsAsync();

        alerts.Should().HaveCount(2);
        alerts.Should().Contain(a => a.Type == "demand_spike" && a.Brand == "Brand A");
        alerts.Should().Contain(a => a.Type == "supply_drop" && a.Brand == "Brand B");
    }

    #endregion

    #region Trace Collector Captures Multi-Agent Flow

    [Fact]
    public void TraceCollector_CapturesMultiAgentFlow()
    {
        string traceId = Guid.NewGuid().ToString("N");
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        // Simulate: routing → demand agent → tool call → response
        var routingSpan = new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, null,
            "agent.routing", t0, t0.AddMilliseconds(25), 25, 100, 50, 0.001m);

        var agentSpan = new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, routingSpan.SpanId,
            "demand-forecasting.handle", t0.AddMilliseconds(25), t0.AddMilliseconds(225), 200, 500, 200, 0.005m);

        var toolSpan = new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, agentSpan.SpanId,
            "tool.get_historical_demand", t0.AddMilliseconds(50), t0.AddMilliseconds(150), 100, 0, 0, 0m);

        var responseSpan = new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, routingSpan.SpanId,
            "agent.response", t0.AddMilliseconds(225), t0.AddMilliseconds(275), 50, 0, 50, 0.001m);

        _traceCollector.CaptureSpan(routingSpan);
        _traceCollector.CaptureSpan(agentSpan);
        _traceCollector.CaptureSpan(toolSpan);
        _traceCollector.CaptureSpan(responseSpan);

        TraceSummary? summary = _traceCollector.GetSummary(traceId);

        summary.Should().NotBeNull();
        summary.Spans.Should().HaveCount(4);
        summary.TotalInputTokens.Should().Be(600);
        summary.TotalOutputTokens.Should().Be(300);
        summary.TotalEstimatedCostUsd.Should().Be(0.007m);
    }

    #endregion

    #region Full Pipeline: Message → Route → Agent → Tools → Memory → Response

    [Fact]
    public async Task FullPipeline_MessageToResponseWithMemoryAndTracing()
    {
        // 1. Route
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.DemandForecasting}\",\"confidence\":0.92,\"intents\":[\"{AgentIntent.DemandForecasting}\"]}}");
        GeneralAgent generalAgent = CreateGeneralAgent(MockChatClient("fallback"));
        ISpecialistAgent demandAgent = CreateMockSpecialist("demand-forecasting", AgentIntent.DemandForecasting,
            "Brand X demand is projected to grow 15%.");
        RetailOpsRouter router = CreateRouter(routerClient, [demandAgent, generalAgent]);

        RoutingDecision routingResult = await router.RouteAsync("Forecast for Brand X", null, null, null);
        routingResult.Intent.Should().Be(AgentIntent.DemandForecasting);

        // 2. Dispatch to specialist
        Contracts.ChatResponse response = await demandAgent.HandleAsync(
            new ChatRequest("Forecast for Brand X", SessionId: "pipeline-test"));
        response.Reply.Should().Contain("Brand X");

        // 3. Store memory
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _memory.StoreAsync("user-1", new MemoryEntry(
            Guid.NewGuid().ToString("N"), "user-1",
            MemoryType.ConversationSummary, "Discussed Brand X demand forecast",
            "Brand X", now, now.AddDays(30)));

        // 4. Verify memory persists
        IReadOnlyList<MemoryEntry> memories = await _memory.RecallAsync("user-1", "Brand X");
        memories.Should().ContainSingle();

        // 5. Trace collector captures the flow
        string traceId = Guid.NewGuid().ToString("N");
        _traceCollector.CaptureSpan(new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, null,
            "agent.routing", now, now.AddMilliseconds(25), 25));
        _traceCollector.CaptureSpan(new TraceSpan(
            Guid.NewGuid().ToString("N"), traceId, null,
            "demand-forecasting.handle", now.AddMilliseconds(25), now.AddMilliseconds(200), 175));

        _traceCollector.GetSpans(traceId).Should().HaveCount(2);
    }

    [Fact]
    public async Task FullPipeline_AlertsDetectedAfterDataChange()
    {
        // Simulate: data change causes anomaly, alert service detects it
        _alertService.SeedDataPoint("Brand X", "West", "demand_spike", baseline: 1000, current: 1500);

        IReadOnlyList<Alert> alerts = await _alertService.CheckForAlertsAsync();
        alerts.Should().ContainSingle();
        alerts[0].Severity.Should().Be("high"); // 50% deviation

        // User dismisses alert
        await _alertService.DismissAsync(alerts[0].Id, "user-1");
        IReadOnlyList<Alert> active = await _alertService.GetActiveForUserAsync("user-1");
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task FullPipeline_MemoryManagementRouting()
    {
        IChatClient routerClient = MockChatClient(
            $"{{\"intent\":\"{AgentIntent.MemoryManagement}\",\"confidence\":0.95,\"intents\":[\"{AgentIntent.MemoryManagement}\"]}}");

        GeneralAgent generalAgent = CreateGeneralAgent(MockChatClient("Done."));
        var memoryAgent = new MemoryManagementAgent(
            Mock.Of<IConversationMemory>(),
            Mock.Of<ILogger<MemoryManagementAgent>>());
        RetailOpsRouter router = CreateRouter(routerClient, [generalAgent, memoryAgent]);

        RoutingDecision result = await router.RouteAsync("Forget everything about me", null, null, null);
        result.Intent.Should().Be(AgentIntent.MemoryManagement);
    }

    #endregion

    #region Helpers

    private static IChatClient MockChatClient(string responseText)
    {
        var mock = new Mock<IChatClient>();
        mock
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, responseText)));
        return mock.Object;
    }

    private static ISpecialistAgent CreateMockSpecialist(string key, string intent, string response = "Mock response")
    {
        var mock = new Mock<ISpecialistAgent>();
        mock.Setup(a => a.Key).Returns(key);
        mock.Setup(a => a.DisplayName).Returns($"Mock {key}");
        mock.Setup(a => a.SupportedIntents).Returns([intent]);
        mock.Setup(a => a.HandleAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Contracts.ChatResponse(response, "session-mock", []));
        return mock.Object;
    }

    private static IHubContext<TelemetryHub> CreateMockHubContext()
    {
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext.Object;
    }

    private static GeneralAgent CreateGeneralAgent(IChatClient? chatClient = null)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient ?? Mock.Of<IChatClient>(),
            CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        return new GeneralAgent(
            pipeline,
            new AgentDefinition { Name = "General", Model = "gpt-4o", SystemPrompt = "Test", Temperature = 0.7 },
            []);
    }

    private static RetailOpsRouter CreateRouter(IChatClient chatClient, IEnumerable<ISpecialistAgent> specialists)
    {
        return new RetailOpsRouter(
            chatClient,
            new AgentDefinition { Name = "Router", Model = "gpt-5.4-mini", SystemPrompt = "Classify intent.", Temperature = 0.1 },
            specialists,
            Mock.Of<ILogger<RetailOpsRouter>>());
    }

    #endregion
}
