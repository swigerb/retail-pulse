using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Budget;

namespace RetailPulse.Tests.Budget;

public sealed class GatewayToolDocumentBudgetChatClientTests
{
    private static GatewayToolDocumentBudgetChatClient CreateBoundary(int maxChars = 9_500) =>
        new(
            Mock.Of<IChatClient>(),
            new ToolResultBudgetOptions
            {
                Enabled = true,
                MaxCumulativeChars = maxChars
            },
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);

    private static ChatMessage ToolMessage(int index, string result) =>
        new(ChatRole.Tool, [new FunctionResultContent($"call-{index}", result)]);

    private static IReadOnlyList<string> ResultTexts(IEnumerable<ChatMessage> messages) =>
        [.. messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => GatewayToolDocumentBudgetChatClient.ResultText(result.Result))];

    [Fact]
    public void PrepareMessages_BoundsActualAggregateAndDocumentCount_WithoutMutatingTranscript()
    {
        List<ChatMessage> transcript =
        [
            new(ChatRole.User, "compare the portfolio"),
            .. Enumerable.Range(1, 6).Select(i => ToolMessage(i, new string((char)('a' + i), 1_800)))
        ];
        IReadOnlyList<string> originalResults = ResultTexts(transcript);

        IReadOnlyList<ChatMessage> outgoing = CreateBoundary().PrepareMessages(
            transcript,
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);
        IReadOnlyList<string> outgoingResults = ResultTexts(outgoing);
        var counted = outgoingResults.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

        counted.Should().HaveCountLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxToolDocuments);
        counted.Sum(text => text.Length).Should()
            .BeLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxGatewayAggregateChars);
        counted.Should().ContainSingle(text => text.Contains("budget_notice", StringComparison.Ordinal));
        metrics.OmittedDocuments.Should().BeGreaterThan(0);
        ResultTexts(transcript).Should().Equal(originalResults,
            "the model-facing copy must not destroy canonical results retained for response processing");
    }

    [Fact]
    public void PrepareMessages_CountsEveryRepeatedResultOccurrence()
    {
        string cachedResult = JsonSerializer.Serialize(new
        {
            brand = "Urban Living",
            values = new string('x', 2_900)
        });
        List<ChatMessage> transcript =
        [
            .. Enumerable.Range(1, 4).Select(i => ToolMessage(i, cachedResult))
        ];

        IReadOnlyList<ChatMessage> outgoing = CreateBoundary().PrepareMessages(
            transcript,
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);
        var counted = ResultTexts(outgoing).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

        metrics.OriginalChars.Should().Be(cachedResult.Length * 4,
            "deduplicated executions still create four outgoing tool documents");
        counted.Sum(text => text.Length).Should().BeLessThanOrEqualTo(9_500);
        metrics.OmittedDocuments.Should().Be(1);
    }

    [Fact]
    public void PrepareMessages_RepeatedDiagnosticsCannotOverflowAggregate()
    {
        string diagnostic = JsonSerializer.Serialize(new
        {
            budget_notice = new string('d', 2_200)
        });
        List<ChatMessage> transcript =
        [
            .. Enumerable.Range(1, 8).Select(i => ToolMessage(i, diagnostic))
        ];

        IReadOnlyList<ChatMessage> outgoing = CreateBoundary().PrepareMessages(
            transcript,
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);
        var counted = ResultTexts(outgoing).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

        counted.Should().HaveCountLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxToolDocuments);
        counted.Sum(text => text.Length).Should().BeLessThanOrEqualTo(9_500);
        metrics.OmittedDocuments.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PrepareMessages_BoundsExemptChartResult_ButPreservesOriginalCanonicalPayload()
    {
        string chart = JsonSerializer.Serialize(new
        {
            type = "grouped-bar",
            title = "Foundry Home vs Urban Living",
            data = new string('c', 10_200)
        });
        ChatMessage original = ToolMessage(1, chart);

        IReadOnlyList<ChatMessage> outgoing = CreateBoundary().PrepareMessages(
            [original],
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);

        ResultTexts(outgoing).Single().Should().Contain("budget_notice");
        ResultTexts([original]).Single().Should().Be(chart,
            "frontend chart reconstruction must retain the canonical CreateChart result");
        metrics.ReturnedChars.Should().BeLessThanOrEqualTo(9_500);
    }

    [Fact]
    public void PrepareMessages_CountsDecodedToolText_NotJsonTransportEscapes()
    {
        string escaped = JsonSerializer.Serialize(new
        {
            narrative = string.Concat(Enumerable.Repeat("quoted \"value\" and path C:\\retail\\data\n", 180))
        });
        List<ChatMessage> transcript =
        [
            ToolMessage(1, escaped),
            ToolMessage(2, escaped)
        ];

        IReadOnlyList<ChatMessage> outgoing = CreateBoundary().PrepareMessages(
            transcript,
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);
        var counted = ResultTexts(outgoing).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

        metrics.OriginalChars.Should().Be(escaped.Length * 2);
        counted.Sum(text => text.Length).Should().BeLessThanOrEqualTo(9_500);

        string serializedTransport = JsonSerializer.Serialize(counted);
        List<string> decoded = JsonSerializer.Deserialize<List<string>>(serializedTransport)!;
        decoded.Sum(text => text.Length).Should().Be(counted.Sum(text => text.Length),
            "APIM parses the request before counting tool-document text");
    }

    [Fact]
    public void BudgetCapNotice_DoesNotClaimPartialResultsAreComplete()
    {
        string notice = BudgetedAIFunction.BuildBudgetCapNotice(5);

        notice.Should().NotContain("COMPLETE");
        notice.Should().Contain("do not claim", Exactly.Once());
    }

    [Fact]
    public async Task FunctionInvocation_FollowUpSendsBoundedCloneToGatewayTransport()
    {
        string canonicalChart = JsonSerializer.Serialize(new
        {
            type = "grouped-bar",
            title = "Foundry Home vs Urban Living",
            data = new string('c', 10_200)
        });
        var observedRequests = new List<IReadOnlyList<ChatMessage>>();
        int transportCalls = 0;
        var transport = new Mock<IChatClient>();
        transport
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                IEnumerable<ChatMessage> messages,
                ChatOptions? _,
                CancellationToken _) =>
            {
                observedRequests.Add([.. messages]);
                transportCalls++;
                return transportCalls == 1
                    ? new ChatResponse(new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("chart-call", "CreateChart", new Dictionary<string, object?>())]))
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Chart ready."));
            });

        AIFunction chartTool = AIFunctionFactory.Create(
            () => canonicalChart,
            "CreateChart",
            "Creates a canonical chart");
        using var boundary = new GatewayToolDocumentBudgetChatClient(
            transport.Object,
            new ToolResultBudgetOptions { Enabled = true, MaxCumulativeChars = 9_500 },
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);
        using var functionClient = new FunctionInvokingChatClient(boundary)
        {
            MaximumIterationsPerRequest = 3
        };

        ChatResponse response = await functionClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "compare the brands")],
            new ChatOptions { Tools = [chartTool] });

        response.Text.Should().Be("Chart ready.");
        observedRequests.Should().HaveCount(2);
        IReadOnlyList<string> gatewayResults = ResultTexts(observedRequests[1]);
        gatewayResults.Should().ContainSingle();
        gatewayResults.Single().Should().Contain("budget_notice");
        gatewayResults.Sum(result => result.Length).Should()
            .BeLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxGatewayAggregateChars);
        canonicalChart.Should().Contain("Foundry Home vs Urban Living",
            "the tool still produced the canonical payload before the transport cloned and bounded it");
    }

    [Fact]
    public async Task StreamingRequest_AppliesSameGatewayBoundary()
    {
        IReadOnlyList<ChatMessage>? observed = null;
        var transport = new Mock<IChatClient>();
        transport
            .Setup(client => client.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback((
                IEnumerable<ChatMessage> messages,
                ChatOptions? _,
                CancellationToken _) => observed = [.. messages])
            .Returns(StreamReply());

        using var boundary = new GatewayToolDocumentBudgetChatClient(
            transport.Object,
            new ToolResultBudgetOptions { Enabled = true, MaxCumulativeChars = 24_000 },
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);
        List<ChatMessage> transcript =
        [
            ToolMessage(1, new string('a', 6_000)),
            ToolMessage(2, new string('b', 6_000))
        ];

        await foreach (ChatResponseUpdate _ in boundary.GetStreamingResponseAsync(transcript))
        {
        }

        observed.Should().NotBeNull();
        var counted = ResultTexts(observed).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        counted.Sum(text => text.Length).Should()
            .BeLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxGatewayAggregateChars,
                "the hard gateway ceiling applies even if configuration is accidentally raised");
    }

    [Fact]
    public void DisabledSemanticCompaction_DoesNotDisableHardGatewayCeiling()
    {
        var boundary = new GatewayToolDocumentBudgetChatClient(
            Mock.Of<IChatClient>(),
            new ToolResultBudgetOptions { Enabled = false, MaxCumulativeChars = 9_500 },
            NullLogger<GatewayToolDocumentBudgetChatClient>.Instance);

        IReadOnlyList<ChatMessage> outgoing = boundary.PrepareMessages(
            [ToolMessage(1, new string('x', 10_100))],
            out GatewayToolDocumentBudgetChatClient.BoundaryMetrics metrics);

        ResultTexts(outgoing).Single().Should().Contain("budget_notice");
        metrics.ReturnedChars.Should()
            .BeLessThanOrEqualTo(GatewayToolDocumentBudgetChatClient.MaxGatewayAggregateChars);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamReply()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        await Task.CompletedTask;
    }
}
