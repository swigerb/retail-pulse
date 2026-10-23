using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Agents;

public sealed class ChartToolLoopTransportTests
{
    private const string ComparisonPrompt =
        "Compare Foundry Home vs Urban Living performance in the West Coast";

    private const string ChartJson = """
        {
          "type":"line",
          "title":"West Coast Depletions: Foundry Home vs Urban Living",
          "xAxisTitle":"Week",
          "yAxisTitle":"Depletion Volume",
          "data":[
            {"legend":"Foundry Home","values":[{"x":"2026-10-05","y":123.5},{"x":"2026-10-12","y":145.25}]},
            {"legend":"Urban Living","values":[{"x":"2026-10-05","y":98.75},{"x":"2026-10-12","y":110.5}]}
          ]
        }
        """;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Pipeline_OffersChartToolOnlyWhenDeliverable_AndPreservesBoundedTransport(
        bool withProgress, bool explicitChart, bool scopedRequest)
    {
        var budgetOptions = new ToolResultBudgetOptions
        {
            Enabled = true,
            MaxCumulativeChars = 9_500,
            MaxResultChars = 6_000,
            MaxToolCalls = 8
        };
        var chartTool = new ChartDataTool(NullLogger<ChartDataTool>.Instance);
        AIFunction createChart = AIFunctionFactory.Create(chartTool.CreateChart);
        AIFunction getPerformance = AIFunctionFactory.Create(
            (int part) => JsonSerializer.Serialize(new { part, evidence = new string('x', 1_800) }),
            "GetPerformance");
        int turns = 0;
        bool chartWithheldFromModel = false;
        List<(string[] Tools, string SystemPrompt, int Documents, int Chars)> observedTurns = [];
        var transport = new Mock<IChatClient>();
        transport.Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken _) =>
            {
                FunctionResultContent[] results = [.. messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()];
                string[] documents = [.. results.Select(result =>
                        GatewayToolDocumentBudgetChatClient.ResultText(result.Result))
                    .Where(text => !string.IsNullOrWhiteSpace(text))];
                observedTurns.Add((
                    [.. options?.Tools?.Select(tool => tool.Name) ?? []],
                    messages.First(message => message.Role == ChatRole.System).Text,
                    documents.Length,
                    documents.Sum(text => text.Length)));

                switch (++turns)
                {
                    case 1:
                        return new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                            [.. Enumerable.Range(0, 5).Select(part => (AIContent)new FunctionCallContent(
                                $"data-{part}", getPerformance.Name,
                                new Dictionary<string, object?> { ["part"] = part }))]));
                    case 2 when !explicitChart:
                        return new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                            "Here is the evidence-backed performance comparison."));
                    case 2:
                        return new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                        [
                            new FunctionCallContent("chart", createChart.Name,
                                new Dictionary<string, object?> { ["chartSpecJson"] = ChartJson })
                        ]));
                    case 3:
                        string chartDocument = GatewayToolDocumentBudgetChatClient.ResultText(
                            results.Single(result => result.CallId == "chart").Result);
                        chartWithheldFromModel = !chartDocument.Contains("West Coast Depletions",
                            StringComparison.Ordinal);
                        return new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                            "I generated the West Coast comparison line chart."));
                    default:
                        throw new InvalidOperationException("Unexpected model turn.");
                }
            });

        using var gateway = new GatewayToolDocumentBudgetChatClient(
            transport.Object, budgetOptions,
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);
        using var functionClient = new FunctionInvokingChatClient(gateway)
        {
            MaximumIterationsPerRequest = 3
        };
        using var capture = new ResponseCaptureClient(functionClient);
        var pipeline = new AgentExecutionPipeline(
            capture, AgentTestFixtures.CreateMockHubContext(), null, null,
            new ConfigurationBuilder().Build(), NullLogger<AgentExecutionPipeline>.Instance,
            null, NoOpAnonymousChatPolicy.Instance, new TenantConfiguration(),
            new ToolResultBudget([]), budgetOptions);
        var toolResults = new List<string>();
        string userIntent = explicitChart ? ComparisonPrompt + " Show a line chart." : ComparisonPrompt;
        var context = new AgentExecutionContext
        {
            AgentName = "demand-forecasting",
            SystemPrompt = "Compare the supplied evidence.",
            Temperature = 0,
            ModelName = "test",
            Request = new ChatRequest(
                scopedRequest ? "Show a bar chart from the comparison data." : userIntent,
                OriginalMessage: scopedRequest ? userIntent : null,
                History: [new ChatHistoryMessage("assistant", "I generated a chart in the earlier turn.")]),
            Tools = [getPerformance, createChart],
            OnToolResult = (text, _) =>
            {
                toolResults.Add(text);
                return Task.CompletedTask;
            }
        };

        Contracts.ChatResponse response = withProgress
            ? await pipeline.ExecuteWithProgressAsync(context)
            : await pipeline.ExecuteAsync(context);

        turns.Should().Be(explicitChart ? 3 : 2);
        foreach ((string[] Tools, string SystemPrompt, int Documents, int Chars) observed in observedTurns)
        {
            observed.Tools.Should().Contain(getPerformance.Name);
            observed.Tools.Contains(createChart.Name).Should().Be(explicitChart);
            observed.Documents.Should().BeLessThanOrEqualTo(5);
            observed.Chars.Should().BeLessThanOrEqualTo(9_500);
            if (!explicitChart)
            {
                observed.SystemPrompt.Should().Contain("This request is prose-only")
                    .And.Contain("Do not generate chart JSON or claim a chart was created")
                    .And.Contain("even if earlier conversation turns mention charts");
            }
        }
        chartWithheldFromModel.Should().Be(explicitChart,
            "the gateway boundary must remain bounded even for an exempt CreateChart result");
        toolResults.Should().HaveCount(explicitChart ? 6 : 5);
        capture.Response.Should().NotBeNull();

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(
            response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        JsonElement charts = serialized.RootElement.GetProperty("charts");
        if (explicitChart)
        {
            toolResults.Should().Contain(text => text.Contains("\"status\":\"success\""));
            ChartSpec extracted = AgentExecutionPipeline.ExtractChartSpecs(capture.Response)
                .Should().ContainSingle().Subject;
            extracted.Should().BeEquivalentTo(JsonSerializer.Deserialize<ChartSpec>(
                ChartJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
            response.Spans.Should().Contain(span => span.Name == createChart.Name);
            response.Charts.Should().ContainSingle().Which.Should().BeEquivalentTo(extracted);
            charts.GetArrayLength().Should().Be(1);
            JsonSerializer.Deserialize<ChartSpec>(charts[0].GetRawText(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                .Should().BeEquivalentTo(extracted);
        }
        else
        {
            AgentExecutionPipeline.ExtractChartSpecs(capture.Response).Should().BeEmpty();
            response.Spans.Should().NotContain(span => span.Name == createChart.Name);
            response.Charts.Should().BeNullOrEmpty(
                "issue #76 explicitly treats the exact issue #428 prompt as prose-only");
            charts.ValueKind.Should().Be(JsonValueKind.Null);
            response.Reply.Should().Be("Here is the evidence-backed performance comparison.");
        }
    }

    [Fact]
    public async Task ActualFactoryToolResult_SurvivesMafExtraction_BeforeGroupASuppression()
    {
        var tool = new ChartDataTool(NullLogger<ChartDataTool>.Instance);
        AIFunction createChart = AIFunctionFactory.Create(tool.CreateChart);
        int turns = 0;
        var transport = new Mock<IChatClient>();
        transport.Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++turns == 1
                ? new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("chart", createChart.Name,
                        new Dictionary<string, object?> { ["chartSpecJson"] = ChartJson })
                ]))
                : new MeaiChatResponse(new ChatMessage(ChatRole.Assistant,
                    "I generated the West Coast comparison line chart.")));
        using var functionClient = new FunctionInvokingChatClient(transport.Object)
        {
            MaximumIterationsPerRequest = 3
        };
        AgentResponse mafResponse = await MafAgentInvoker.RunAsync(
            functionClient, "transport-characterization",
            [new ChatMessage(ChatRole.User, ComparisonPrompt)],
            new ChatOptions { Tools = [createChart] }, null, CancellationToken.None);
        MeaiChatResponse response = mafResponse.AsChatResponse();
        List<ChartSpec> extracted = AgentExecutionPipeline.ExtractChartSpecs(response);
        extracted.Should().ContainSingle().Which.Should().BeEquivalentTo(
            JsonSerializer.Deserialize<ChartSpec>(ChartJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
        var pipeline = new AgentExecutionPipeline(functionClient,
            AgentTestFixtures.CreateMockHubContext(), new ConfigurationBuilder().Build(),
            NullLogger<AgentExecutionPipeline>.Instance);

        AgentExecutionPipeline.ChartFulfillmentResult fulfilled =
            pipeline.EnforceChartFulfillment(ComparisonPrompt, response, extracted, response.Text);

        turns.Should().Be(2);
        fulfilled.Charts.Should().BeEmpty(
            "the chart is intentionally suppressed by Group A, not lost by transport or extraction");
        fulfilled.Reply.Should().Be(response.Text,
            "Group A historically leaves the model's chart-created claim untouched");
    }

    private sealed class ResponseCaptureClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public MeaiChatResponse? Response { get; private set; }

        public override async Task<MeaiChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Response = await InnerClient.GetResponseAsync(messages, options, cancellationToken);
            return Response;
        }
    }
}
