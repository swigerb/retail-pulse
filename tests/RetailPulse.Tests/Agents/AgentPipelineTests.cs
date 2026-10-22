using System.ClientModel;
using System.ClientModel.Primitives;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Tests for the shared IAgentExecutionPipeline / AgentExecutionPipeline,
/// covering message construction, history truncation, and token accounting.
/// </summary>
public class AgentPipelineTests
{
    #region BuildMessages — basic construction

    [Fact]
    public async Task BuildMessages_NoHistory_ReturnsTwoMessages()
    {
        var request = new ChatRequest("What are today's sales?");
        List<ChatMessage> messages = AgentExecutionPipeline.BuildMessages("You are a test agent.", request);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Be("You are a test agent.");
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("What are today's sales?");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_WithHistory_InsertsBeforeUserMessage()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("user", "First question"),
            new("assistant", "First answer"),
        };

        var request = new ChatRequest("Follow-up", History: history);
        List<ChatMessage> messages = AgentExecutionPipeline.BuildMessages("System prompt.", request);

        // System + 2 history + user = 4
        messages.Should().HaveCount(4);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("First question");
        messages[2].Role.Should().Be(ChatRole.Assistant);
        messages[2].Text.Should().Be("First answer");
        messages[3].Role.Should().Be(ChatRole.User);
        messages[3].Text.Should().Be("Follow-up");
        await Task.CompletedTask;
    }

    #endregion

    #region BuildMessages — history truncation

    [Fact]
    public async Task BuildMessages_TruncatesHistoryToMaxTurns()
    {
        // maxTurns is 10 in the pipeline, so max history messages = 20
        var history = Enumerable.Range(1, 30)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"Message {i}"))
            .ToList();

        var request = new ChatRequest("Latest question", History: history);
        List<ChatMessage> messages = AgentExecutionPipeline.BuildMessages("System.", request);

        // System (1) + truncated history (20) + user (1) = 22
        messages.Should().HaveCount(22);

        // Truncation keeps the last 20 history messages (indices 10–29)
        messages[1].Text.Should().Be("Message 11", "truncation should keep the most recent history");
        messages[^1].Text.Should().Be("Latest question");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_HistoryExactlyAtLimit_NoTruncation()
    {
        var history = Enumerable.Range(1, 20)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "assistant" : "user", $"Msg {i}"))
            .ToList();

        var request = new ChatRequest("Current", History: history);
        List<ChatMessage> messages = AgentExecutionPipeline.BuildMessages("Sys.", request);

        // System (1) + 20 history + user (1) = 22
        messages.Should().HaveCount(22);
        messages[1].Text.Should().Be("Msg 1", "no truncation needed — all history fits");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildMessages_HistoryUnderLimit_KeepsAll()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("user", "A"),
            new("assistant", "B"),
            new("user", "C"),
            new("assistant", "D"),
        };

        var request = new ChatRequest("E", History: history);
        List<ChatMessage> messages = AgentExecutionPipeline.BuildMessages("Sys.", request);

        messages.Should().HaveCount(6); // 1 system + 4 history + 1 user
        await Task.CompletedTask;
    }

    #endregion

    #region IAgentExecutionPipeline — interface contract

    [Fact]
    public async Task ExecuteAsync_ReturnsNonNullResponse()
    {
        AgentExecutionPipeline pipeline = CreatePipeline("Test reply.");

        AgentExecutionContext context = CreateContext("What's up?");
        Contracts.ChatResponse response = await pipeline.ExecuteAsync(context);

        response.Should().NotBeNull();
        response.Reply.Should().Be("Test reply.");
        response.SessionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UsesProvidedSessionId()
    {
        AgentExecutionPipeline pipeline = CreatePipeline("Reply.");
        var request = new ChatRequest("Hi", SessionId: "my-session-123");
        AgentExecutionContext context = CreateContext(request: request);

        Contracts.ChatResponse response = await pipeline.ExecuteAsync(context);

        response.SessionId.Should().Be("my-session-123");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFallbackReply_WhenLlmReturnsNull()
    {
        // When the LLM returns a response with null Text, the pipeline
        // uses the fallback reply. We mock a response where Text is null
        // by setting up a response message with no text content.
        var chatClient = new Mock<IChatClient>();
        var nullTextResponse = new Microsoft.Extensions.AI.ChatResponse(
            new ChatMessage(ChatRole.Assistant, (string?)null));
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(nullTextResponse);

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var pipeline = new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());

        AgentExecutionContext context = CreateContext("Question", fallbackReply: "Custom fallback");
        Contracts.ChatResponse response = await pipeline.ExecuteAsync(context);

        // The pipeline uses: string.IsNullOrWhiteSpace(response.Text) ? context.FallbackReply : response.Text
        // When the LLM returns null/empty text, the fallback reply is used.
        response.Reply.Should().Be("Custom fallback");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WhenGatewayRateLimits_RethrowsForEndpoint429Shaping(bool withProgress)
    {
        var rawResponse = new Mock<PipelineResponse>();
        rawResponse.SetupGet(response => response.Status).Returns(429);
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("rate limited", rawResponse.Object, null));

        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pipeline = new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
        AgentExecutionContext context = CreateContext("Show me Urban Living depletion trends");

        Func<Task> act = () => withProgress
            ? pipeline.ExecuteWithProgressAsync(context)
            : pipeline.ExecuteAsync(context);

        ClientResultException exception = (await act.Should().ThrowAsync<ClientResultException>()).Which;
        exception.Status.Should().Be(429,
            "the endpoint must receive the typed throttle and return HTTP 429 with gateway evidence");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CompleteCuratedPrefetch_RemovesToolsAndBuildsChart(bool withProgress)
    {
        ChatOptions? observedOptions = null;
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => observedOptions = options)
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Here is the comparison.")));

        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pipeline = new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
        AIFunction tool = AIFunctionFactory.Create(
            (string brand) => brand,
            "GetHistoricalDemand");
        const string prompt = "Compare Coastline Tacos vs Apex Grill depletions across all regions";
        var context = new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a test assistant.",
            Temperature = 0.2f,
            ModelName = "test-model",
            Request = new ChatRequest(prompt),
            Tools = [tool],
            PrefetchedData = new Dictionary<string, string>
            {
                ["GetHistoricalDemand:Coastline Tacos"] = DemandPayload("Coastline Tacos", 900),
                ["GetHistoricalDemand:Apex Grill"] = DemandPayload("Apex Grill", 700),
            },
        };

        Contracts.ChatResponse response = withProgress
            ? await pipeline.ExecuteWithProgressAsync(context)
            : await pipeline.ExecuteAsync(context);

        observedOptions.Should().NotBeNull();
        observedOptions.Tools.Should().BeEmpty(
            "a deterministically chart-complete prefetch must make further model tool calls impossible");
        response.Charts.Should().ContainSingle();
        response.Charts[0].Type.Should().Be("groupedBar");
        response.Charts[0].Data.Select(s => s.Legend)
            .Should().BeEquivalentTo(["Coastline Tacos", "Apex Grill"]);
        response.Spans.Count(s => s.Type == "tool_call").Should().Be(2);
        response.Spans.Count(s => s.Type == "tool_result").Should().Be(2);
        response.Spans.Where(s => s.Type == "tool_call").Select(s => s.Name)
            .Should().OnlyContain(name => name == "GetHistoricalDemand");
        response.Spans.Where(s => s.Type == "tool_result").Select(s => s.Detail)
            .Should().OnlyContain(detail => detail.Contains("Prefetched result available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_IncompleteCuratedPrefetch_KeepsRecoveryToolsAvailable()
    {
        ChatOptions? observedOptions = null;
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => observedOptions = options)
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new ChatMessage(ChatRole.Assistant, "I need the second brand.")));

        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var pipeline = new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
        AIFunction tool = AIFunctionFactory.Create(
            (string brand) => brand,
            "GetHistoricalDemand");
        const string prompt = "Compare Coastline Tacos vs Apex Grill depletions across all regions";
        AgentExecutionContext context = CreateContext(request: new ChatRequest(prompt)) with
        {
            Tools = [tool],
            PrefetchedData = new Dictionary<string, string>
            {
                ["GetHistoricalDemand:Coastline Tacos"] = DemandPayload("Coastline Tacos", 900),
            },
        };

        Contracts.ChatResponse response = await pipeline.ExecuteAsync(context);

        observedOptions.Should().NotBeNull();
        observedOptions.Tools.Should().ContainSingle(
            "partial prefetch must preserve the normal recovery tool path");
        response.Spans.Should().Contain(s =>
            s.Type == "tool_call" && s.Name == "GetHistoricalDemand"
            && s.Detail.Contains("Prefetched", StringComparison.Ordinal));
    }

    #endregion

    #region Token accounting — model name correctness

    [Fact]
    public Task BuildTokenUsage_UsesActualModelName_NotHardcoded()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:gpt-4.1-mini:InputPerMillion"] = "0.40",
                ["TokenPricing:gpt-4.1-mini:OutputPerMillion"] = "1.60",
            })
            .Build();

        AgentExecutionPipeline pipeline = CreatePipeline("Reply.", config: config);

        TokenUsage usage = pipeline.BuildTokenUsage(1000, 500, 1500, "gpt-4.1-mini");

        usage.InputTokens.Should().Be(1000);
        usage.OutputTokens.Should().Be(500);
        usage.TotalTokens.Should().Be(1500);
        usage.EstimatedCostUsd.Should().NotBeNull("pricing config exists for gpt-4.1-mini");
        usage.EstimatedCostUsd.Should().BeGreaterThan(0);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BuildTokenUsage_ReturnsNullCost_WhenModelNotInConfig()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        AgentExecutionPipeline pipeline = CreatePipeline("Reply.", config: config);

        TokenUsage usage = pipeline.BuildTokenUsage(100, 50, 150, "unknown-model");

        usage.EstimatedCostUsd.Should().BeNull(
            "no pricing config for 'unknown-model' — cost should be null, not a hardcoded value");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BuildTokenUsage_CalculatesCorrectCost()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenPricing:test-model:InputPerMillion"] = "2.00",
                ["TokenPricing:test-model:OutputPerMillion"] = "8.00",
            })
            .Build();

        AgentExecutionPipeline pipeline = CreatePipeline("Reply.", config: config);

        TokenUsage usage = pipeline.BuildTokenUsage(1_000_000, 500_000, 1_500_000, "test-model");

        // Expected: (1M * 2.00 / 1M) + (500K * 8.00 / 1M) = 2.00 + 4.00 = 6.00
        usage.EstimatedCostUsd.Should().Be(6.00m);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AgentExecutionContext_ModelName_FlowsThrough()
    {
        // Verify that the context model name is used (not a hardcoded string)
        var context = new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "System prompt.",
            Temperature = 0.5f,
            ModelName = "gpt-5.4-mini",
            Request = new ChatRequest("Test"),
            Tools = [],
        };

        context.ModelName.Should().Be("gpt-5.4-mini");
        context.ModelName.Should().NotBe("gpt-4o", "model name should not be hardcoded");
        await Task.CompletedTask;
    }

    #endregion

    #region Helper methods

    private static AgentExecutionPipeline CreatePipeline(
        string? responseText = "Default reply.",
        IConfiguration? config = null)
    {
        var chatClient = new Mock<IChatClient>();
        var responseMsg = new ChatMessage(ChatRole.Assistant, responseText);
        chatClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(responseMsg));

        config ??= new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        return new AgentExecutionPipeline(
            chatClient.Object,
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>());
    }

    private static AgentExecutionContext CreateContext(
        string message = "Hello",
        string fallbackReply = "I wasn't able to generate a response.",
        ChatRequest? request = null)
    {
        return new AgentExecutionContext
        {
            AgentName = "test-agent",
            SystemPrompt = "You are a test assistant.",
            Temperature = 0.7f,
            ModelName = "test-model",
            Request = request ?? new ChatRequest(message),
            Tools = [],
            FallbackReply = fallbackReply,
        };
    }

    private static string DemandPayload(string brand, double volume) =>
        $$"""{"period":{"label":"last-12-months","start":"2025-05-13","end":"2026-05-13","months":12},"filters":{"brand":"{{brand}}","region":"National","channel":"All"},"summary":{"total_volume":{{volume * 52}},"total_units":1000,"weeks_of_data":52,"avg_weekly_volume":{{volume}}},"by_region":[{"region":"Northeast","volume":{{volume * 52}},"avg_weekly_volume":{{volume}}},{"region":"Southeast","volume":{{(volume - 10) * 52}},"avg_weekly_volume":{{volume - 10}}},{"region":"Midwest","volume":{{(volume - 20) * 52}},"avg_weekly_volume":{{volume - 20}}},{"region":"Southwest","volume":{{(volume - 30) * 52}},"avg_weekly_volume":{{volume - 30}}},{"region":"West Coast","volume":{{(volume - 40) * 52}},"avg_weekly_volume":{{volume - 40}}},{"region":"Pacific Northwest","volume":{{(volume - 50) * 52}},"avg_weekly_volume":{{volume - 50}}}]}""";

    #endregion

    #region SanitizeReplyText — function call leakage filtering

    [Fact]
    public void SanitizeReplyText_RemovesFunctionCallLeakage()
    {
        string dirty = "to=functions.IdentifyDemandRisks 天天中彩票提現 福利彩票天天彩json {\"brand\":\"Apex Grill\",\"region\":\"Southwest\",\"channel\":\"All\"}\nApex Grill is performing well in the Southwest.";
        string clean = AgentExecutionPipeline.SanitizeReplyText(dirty);
        clean.Should().NotContain("to=functions");
        clean.Should().Contain("Apex Grill is performing well");
    }

    [Fact]
    public void SanitizeReplyText_RemovesCorruptedCjkLines()
    {
        string dirty = "天天中彩票提現 福利彩票天天彩json garbage\nActual response content here.";
        string clean = AgentExecutionPipeline.SanitizeReplyText(dirty);
        clean.Should().NotContain("天天");
        clean.Should().Contain("Actual response content here.");
    }

    [Fact]
    public void SanitizeReplyText_PreservesCleanContent()
    {
        string clean = "Here's the demand forecast for Apex Grill in Q2.";
        AgentExecutionPipeline.SanitizeReplyText(clean).Should().Be(clean);
    }

    [Fact]
    public void SanitizeReplyText_ReturnsGracefulMessage_WhenEntireReplyIsGarbage()
    {
        string garbage = "to=functions.Foo blah blah";
        string result = AgentExecutionPipeline.SanitizeReplyText(garbage);
        result.Should().Contain("unable to generate a response");
    }

    #endregion

    #region ExtractInlineCharts — chart JSON echoed as prose

    // The exact failure from the production screenshot: the model narrated its
    // CreateChart payload as raw JSON at the top of the reply, using the alternate
    // Chart.js-style schema (data:{labels,series}) that the tool path cannot bind.
    private const string ScreenshotReply =
        """
        {"type":"bar","title":"Consolidation Check 2026-08-05","data":{"labels":["ClearDesk Vodka","Sierra Gold Tequila","Apex Reserve"],"series":[{"name":"Depletion Velocity","values":[12.5,9.8,7.2]}]},"options":{"orientation":"horizontal","xAxisLabel":"Cases per Week","yAxisLabel":"Brand"}}

        Here's the depletion velocity comparison for all spirits brands in the Northeast. ClearDesk Vodka leads at 12.5 cases per week.
        """;

    [Fact]
    public void ExtractInlineCharts_ScreenshotPayload_StripsRawJsonFromReply()
    {
        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(ScreenshotReply);

        result.Reply.Should().NotContain("\"type\":\"bar\"");
        result.Reply.Should().NotContain("\"series\"");
        result.Reply.Should().NotContain("\"labels\"");
        result.Reply.Should().NotContain("{");
        result.Reply.Should().Contain("Here's the depletion velocity comparison");
        result.Reply.Should().Contain("ClearDesk Vodka leads at 12.5 cases per week.");
    }

    [Fact]
    public void ExtractInlineCharts_ScreenshotPayload_ReturnsStructuredChart()
    {
        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(ScreenshotReply);

        result.Charts.Should().ContainSingle("the inline chart JSON must become structured chart data");
        ChartSpec chart = result.Charts[0];
        chart.Type.Should().Be("horizontalBar", "options.orientation=horizontal maps a bar to horizontalBar");
        chart.Title.Should().Be("Consolidation Check 2026-08-05");
        chart.XAxisTitle.Should().Be("Cases per Week");
        chart.YAxisTitle.Should().Be("Brand");
        chart.Data.Should().ContainSingle();
        chart.Data[0].Legend.Should().Be("Depletion Velocity");
        chart.Data[0].Values.Should().HaveCount(3);
        chart.Data[0].Values[0].X.Should().Be("ClearDesk Vodka");
        chart.Data[0].Values[0].Y.Should().Be(12.5);
        chart.Data[0].Values[2].X.Should().Be("Apex Reserve");
        chart.Data[0].Values[2].Y.Should().Be(7.2);
    }

    [Fact]
    public void ExtractInlineCharts_CanonicalInlineJson_StripsAndExtracts()
    {
        string reply =
            """
            Here is the breakdown.

            {"type":"bar","title":"Monthly Sales","xAxisTitle":"Month","yAxisTitle":"Cases","data":[{"legend":"Sierra Gold","values":[{"x":"Jan","y":1200},{"x":"Feb","y":1450}]}]}
            """;

        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(reply);

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("bar");
        result.Charts[0].Title.Should().Be("Monthly Sales");
        result.Reply.Should().Be("Here is the breakdown.");
        result.Reply.Should().NotContain("{");
    }

    [Fact]
    public void ExtractInlineCharts_CleanProse_ReturnedUnchangedWithNoCharts()
    {
        string reply = "Depletion velocity for spirits brands in the Northeast is trending up 4% this quarter.";

        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(reply);

        result.Reply.Should().Be(reply);
        result.Charts.Should().BeEmpty();
    }

    [Fact]
    public void ExtractInlineCharts_NonChartJson_LeftUntouched()
    {
        // Well-formed JSON that is not a chart must be surfaced, not silently hidden.
        string reply = """The raw metric payload was {"brand":"Apex Grill","region":"Northeast","velocity":7.2} for reference.""";

        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(reply);

        result.Charts.Should().BeEmpty("non-chart JSON must not be treated as a chart");
        result.Reply.Should().Contain("\"brand\":\"Apex Grill\"", "arbitrary JSON is left visible, not stripped");
    }

    [Fact]
    public void ExtractInlineCharts_EmptyReply_ReturnsEmpty()
    {
        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(string.Empty);

        result.Reply.Should().BeEmpty();
        result.Charts.Should().BeEmpty();
    }

    [Fact]
    public void ExtractInlineCharts_FencedFullConfigVariant_StripsFenceAndExtracts()
    {
        // Third real-world variant observed live: a ```json fenced block using the
        // full-config schema (top-level series + xAxis.categories). The fence and JSON
        // must be removed from the reply and promoted to a structured chart.
        string reply =
            """
            Here's the bar chart comparing depletion velocity for all spirits brands in the Northeast.

            ```json
            {"type":"bar","title":"Depletion Velocity","xAxis":{"label":"Brand","categories":["Sierra Gold","Ridgeline","Summit"]},"yAxis":{"label":"Avg Weekly Volume"},"series":[{"name":"Avg Weekly Volume","data":[1893.2,2109.5,2296.3]}]}
            ```
            """;

        AgentExecutionPipeline.InlineChartExtraction result = AgentExecutionPipeline.ExtractInlineCharts(reply);

        result.Reply.Should().NotContain("{");
        result.Reply.Should().NotContain("```");
        result.Reply.Should().Contain("Here's the bar chart comparing depletion velocity");
        result.Charts.Should().ContainSingle();
        ChartSpec chart = result.Charts[0];
        chart.Type.Should().Be("bar");
        chart.XAxisTitle.Should().Be("Brand");
        chart.YAxisTitle.Should().Be("Avg Weekly Volume");
        chart.Data.Should().ContainSingle();
        chart.Data[0].Values.Should().HaveCount(3);
        chart.Data[0].Values[0].X.Should().Be("Sierra Gold");
        chart.Data[0].Values[0].Y.Should().Be(1893.2);
        chart.Data[0].Values[2].X.Should().Be("Summit");
        chart.Data[0].Values[2].Y.Should().Be(2296.3);
    }

    #endregion

    #region MergeInlineCharts — dedup of inline echoes vs. distinct charts

    private static ChartSpec BarChart(string title, string legend, params (string X, double Y)[] points) =>
        new()
        {
            Type = "bar",
            Title = title,
            Data =
            [
                new ChartSeries
                {
                    Legend = legend,
                    Values = [.. points.Select(p => new ChartDataPoint { X = p.X, Y = p.Y })],
                },
            ],
        };

    [Fact]
    public void MergeInlineCharts_NoInlineCharts_ReturnsToolChartsUnchanged()
    {
        var tool = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 10)) };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts(tool, []);

        merged.Should().BeSameAs(tool, "no inline charts means the tool set is returned as-is");
    }

    [Fact]
    public void MergeInlineCharts_DuplicateEcho_IsSuppressed()
    {
        // The model called the tool AND echoed the identical chart JSON in prose.
        // The echo must not produce a second, duplicate render.
        var tool = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 10), ("Feb", 12)) };
        var inline = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 10), ("Feb", 12)) };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts(tool, inline);

        merged.Should().ContainSingle("a genuine duplicate of a tool-produced chart is suppressed");
        merged[0].Title.Should().Be("Sales");
    }

    [Fact]
    public void MergeInlineCharts_DistinctInlineChart_IsPreserved()
    {
        // Tool produced chart A; prose contained a DIFFERENT valid chart B. Both
        // must survive — B must not be silently dropped just because A exists.
        var tool = new List<ChartSpec> { BarChart("Chart A", "A", ("Jan", 10)) };
        var inline = new List<ChartSpec> { BarChart("Chart B", "B", ("Feb", 20)) };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts(tool, inline);

        merged.Should().HaveCount(2);
        merged.Select(c => c.Title).Should().Equal("Chart A", "Chart B");
    }

    [Fact]
    public void MergeInlineCharts_NoToolCharts_PromotesAllInline()
    {
        var inline = new List<ChartSpec> { BarChart("Only", "A", ("Jan", 10)) };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts([], inline);

        merged.Should().ContainSingle();
        merged[0].Title.Should().Be("Only");
    }

    [Fact]
    public void MergeInlineCharts_MixedDuplicateAndDistinct_KeepsOnlyDistinct()
    {
        // Prose echoed the tool chart (suppress) plus a distinct chart (keep).
        var tool = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 10)) };
        var inline = new List<ChartSpec>
        {
            BarChart("Sales", "A", ("Jan", 10)),   // duplicate echo
            BarChart("Forecast", "B", ("Feb", 20)), // distinct
        };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts(tool, inline);

        merged.Should().HaveCount(2);
        merged.Select(c => c.Title).Should().Equal("Sales", "Forecast");
    }

    [Fact]
    public void MergeInlineCharts_DuplicateInlineCharts_AreCollapsed()
    {
        // Two identical inline charts (no tool charts) collapse to one.
        var inline = new List<ChartSpec>
        {
            BarChart("Sales", "A", ("Jan", 10)),
            BarChart("Sales", "A", ("Jan", 10)),
        };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts([], inline);

        merged.Should().ContainSingle("identical inline charts must not double-render");
    }

    [Fact]
    public void MergeInlineCharts_SameTitleDifferentData_IsNotADuplicate()
    {
        // Same title but different datapoints is a distinct chart, not an echo.
        var tool = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 10)) };
        var inline = new List<ChartSpec> { BarChart("Sales", "A", ("Jan", 99)) };

        List<ChartSpec> merged = AgentExecutionPipeline.MergeInlineCharts(tool, inline);

        merged.Should().HaveCount(2, "differing datapoints make it a distinct chart, not a duplicate");
    }

    #endregion

    #region ChartSpecSemanticComparer — content-based equality

    [Fact]
    public void ChartSpecSemanticComparer_IdenticalContent_AreEqual()
    {
        ChartSpec a = BarChart("Sales", "A", ("Jan", 10), ("Feb", 12));
        ChartSpec b = BarChart("Sales", "A", ("Jan", 10), ("Feb", 12));

        Api.Charts.ChartSpecSemanticComparer.Instance.Equals(a, b)
            .Should().BeTrue("structurally identical charts from different sources are equal");
        Api.Charts.ChartSpecSemanticComparer.Instance.GetHashCode(a)
            .Should().Be(Api.Charts.ChartSpecSemanticComparer.Instance.GetHashCode(b));
    }

    [Fact]
    public void ChartSpecSemanticComparer_DifferentValues_AreNotEqual()
    {
        ChartSpec a = BarChart("Sales", "A", ("Jan", 10));
        ChartSpec b = BarChart("Sales", "A", ("Jan", 11));

        Api.Charts.ChartSpecSemanticComparer.Instance.Equals(a, b).Should().BeFalse();
    }

    [Fact]
    public void ChartSpecSemanticComparer_DefaultRecordEquality_WouldFail_ButComparerSucceeds()
    {
        // Guards the reason this comparer exists: record equality compares the
        // List<> members by reference, so two equal-content charts are NOT equal
        // under the compiler-generated Equals.
        ChartSpec a = BarChart("Sales", "A", ("Jan", 10));
        ChartSpec b = BarChart("Sales", "A", ("Jan", 10));

        a.Equals(b).Should().BeFalse("record equality compares List<> Data by reference");
        Api.Charts.ChartSpecSemanticComparer.Instance.Equals(a, b).Should().BeTrue();
    }

    #endregion
}
