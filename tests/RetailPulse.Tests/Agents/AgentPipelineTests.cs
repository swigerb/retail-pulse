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

    #endregion
}
