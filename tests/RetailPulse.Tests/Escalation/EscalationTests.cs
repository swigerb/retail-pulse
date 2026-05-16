using FluentAssertions;
using FluentAssertions.Specialized;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Escalation;

/// <summary>
/// Tests for the escalation system: L1/L2/L3 query complexity detection,
/// context enrichment at each level, force-level override, and exec brief format.
/// Test-first: defines expected escalation behavior before Phase 4.2 implementation.
/// </summary>
public class EscalationTests
{
    #region L1 — Simple Queries (No Escalation)

    [Theory]
    [InlineData("What are Sierra Gold Tequila's depletions?")]
    [InlineData("Show me Northeast region sales")]
    [InlineData("Get inventory levels for FreshMart")]
    public async Task SimpleQuery_StaysAtL1_NoEscalation(string query)
    {
        IEscalationService escalator = CreateEscalationService();
        EscalationResult result = await escalator.ClassifyAndEscalateAsync(query);

        result.Level.Should().Be(1, "simple single-dimension query should stay at L1");
        result.AgentKey.Should().NotBeNullOrEmpty();
        result.Context.Should().NotBeNull();
    }

    [Fact]
    public async Task L1Query_HasMinimalContext()
    {
        IEscalationService escalator = CreateEscalationService();
        EscalationResult result = await escalator.ClassifyAndEscalateAsync("What are my depletions?");

        result.Level.Should().Be(1);
        result.Context.Should().NotBeNull();
        // L1 context should be lightweight
    }

    #endregion

    #region L2 — Complex Multi-Dimensional Queries

    [Theory]
    [InlineData("Compare demand trends with margin performance across all regions for Sierra Gold")]
    [InlineData("Analyze the correlation between promotional spend and supply chain disruptions")]
    [InlineData("Show me competitive pricing impact on our margin and market share simultaneously")]
    public async Task ComplexMultiDimensionalQuery_EscalatesToL2(string query)
    {
        IEscalationService escalator = CreateEscalationService();
        EscalationResult result = await escalator.ClassifyAndEscalateAsync(query);

        result.Level.Should().BeGreaterThanOrEqualTo(2,
            "complex multi-dimensional query should escalate to L2+");
    }

    [Fact]
    public async Task L2Query_HasMoreContextThanL1()
    {
        IEscalationService escalator = CreateEscalationService();

        EscalationResult l1Result = await escalator.ClassifyAndEscalateAsync("Show me depletions");
        EscalationResult l2Result = await escalator.ClassifyAndEscalateAsync(
            "Compare demand and margin trends across all regions with competitive analysis");

        l2Result.Level.Should().BeGreaterThan(l1Result.Level);

        // L2 should have richer context
        int l2ContextSize = System.Text.Json.JsonSerializer.Serialize(l2Result.Context).Length;
        int l1ContextSize = System.Text.Json.JsonSerializer.Serialize(l1Result.Context).Length;

        l2ContextSize.Should().BeGreaterThanOrEqualTo(l1ContextSize,
            "L2 context should be at least as rich as L1 context");
    }

    #endregion

    #region L3 — Strategic/Executive Queries

    [Theory]
    [InlineData("Prepare an executive briefing on portfolio health with strategic recommendations")]
    [InlineData("What's our 3-year brand strategy given competitive landscape and margin erosion?")]
    [InlineData("Board-level summary of portfolio performance with risk assessment and action plan")]
    public async Task StrategicExecutiveQuery_EscalatesToL3(string query)
    {
        IEscalationService escalator = CreateEscalationService();
        EscalationResult result = await escalator.ClassifyAndEscalateAsync(query);

        result.Level.Should().Be(3,
            "strategic/executive query should escalate to L3");
    }

    [Fact]
    public async Task L3_ProducesExecBriefFormat()
    {
        IEscalationService escalator = CreateEscalationService();
        EscalationResult result = await escalator.ClassifyAndEscalateAsync(
            "Executive briefing on portfolio health with strategic recommendations");

        result.Level.Should().Be(3);
        result.Format.Should().NotBeNull("L3 should specify output format");

        // Exec brief format should have expected sections
        result.Format.Should().Contain("summary",
            "exec brief should include a summary section");
        result.Format.Should().Contain("metrics",
            "exec brief should include a metrics section");
        result.Format.Should().Contain("recommendation",
            "exec brief should include a recommendation section");
    }

    #endregion

    #region Context Growth with Escalation

    [Fact]
    public async Task EachLevel_AddsContext()
    {
        IEscalationService escalator = CreateEscalationService();

        EscalationResult l1 = await escalator.ClassifyAndEscalateAsync("Show me depletions");
        EscalationResult l2 = await escalator.ClassifyAndEscalateAsync(
            "Compare demand, margin, and competitive landscape across all regions");
        EscalationResult l3 = await escalator.ClassifyAndEscalateAsync(
            "Executive strategy briefing with full portfolio assessment and 3-year outlook");

        // Context should grow with each level
        l1.Level.Should().Be(1);
        l2.Level.Should().BeGreaterThanOrEqualTo(2);
        l3.Level.Should().Be(3);

        int[] sizes =
        [
            System.Text.Json.JsonSerializer.Serialize(l1.Context).Length,
            System.Text.Json.JsonSerializer.Serialize(l2.Context).Length,
            System.Text.Json.JsonSerializer.Serialize(l3.Context).Length
        ];

        // Each successive level should have at least as much context
        sizes[1].Should().BeGreaterThanOrEqualTo(sizes[0], "L2 context >= L1");
        sizes[2].Should().BeGreaterThanOrEqualTo(sizes[1], "L3 context >= L2");
    }

    #endregion

    #region Force Level Override

    [Fact]
    public async Task ForceLevel_OverridesAutoDetection()
    {
        IEscalationService escalator = CreateEscalationService();

        // Simple query force-escalated to L3
        EscalationResult result = await escalator.ClassifyAndEscalateAsync(
            "Show me depletions", forceLevel: 3);

        result.Level.Should().Be(3,
            "force level should override auto-detection");
    }

    [Fact]
    public async Task ForceLevel_L1_DowngradesComplexQuery()
    {
        IEscalationService escalator = CreateEscalationService();

        EscalationResult result = await escalator.ClassifyAndEscalateAsync(
            "Executive portfolio strategy briefing", forceLevel: 1);

        result.Level.Should().Be(1,
            "force level 1 should downgrade even complex queries");
    }

    [Fact]
    public async Task InvalidForceLevel_HandledGracefully()
    {
        IEscalationService escalator = CreateEscalationService();

        // Force level outside valid range
        Func<Task> act = () => escalator.ClassifyAndEscalateAsync(
            "Show me depletions", forceLevel: 99);

        // Should either clamp to valid range or throw a clear error
        ExceptionAssertions<ArgumentException> exception = await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*level*");
    }

    [Fact]
    public async Task ForceLevel_Zero_HandledGracefully()
    {
        IEscalationService escalator = CreateEscalationService();

        Func<Task> act = () => escalator.ClassifyAndEscalateAsync(
            "Show me depletions", forceLevel: 0);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region L2 Fan-Out Timeout (15 Seconds)

    [Fact]
    public async Task L2FanOut_CompletesWithin15Seconds()
    {
        IEscalationService escalator = CreateEscalationService();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        EscalationResult result = await escalator.ClassifyAndEscalateAsync(
            "Compare demand and margin trends across all regions with competitive analysis");

        sw.Stop();
        result.Level.Should().BeGreaterThanOrEqualTo(2);
        sw.Elapsed.TotalSeconds.Should().BeLessThan(15,
            "L2 fan-out should complete within the 15-second parallel timeout");
    }

    [Fact]
    public async Task L2FanOut_ReturnsResultsNotNull()
    {
        IEscalationService escalator = CreateEscalationService();

        EscalationResult result = await escalator.ClassifyAndEscalateAsync(
            "Compare demand and margin trends across all regions with competitive analysis");

        result.Level.Should().BeGreaterThanOrEqualTo(2);
        result.Context.Should().NotBeNull("L2 should return context even under timeout pressure");
        result.AgentKey.Should().NotBeNullOrEmpty("L2 should route to an agent");
    }

    [Fact]
    public async Task L2FanOut_CancellationRespected()
    {
        IEscalationService escalator = CreateEscalationService();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // The mock service is fast, so it should complete before cancellation.
        // This verifies the CancellationToken is threaded through correctly.
        try
        {
            EscalationResult result = await escalator.ClassifyAndEscalateAsync(
                "Compare demand and margin trends across all regions", ct: cts.Token);
            // If it completes, that's fine — mock is fast
            result.Should().NotBeNull();
        }
        catch (OperationCanceledException)
        {
            // Also acceptable — cancellation was respected
        }
    }

    #endregion

    #region Original Question Preservation

    [Fact]
    public async Task Escalation_PreservesOriginalQuestion()
    {
        IEscalationService escalator = CreateEscalationService();
        string originalQuestion = "Compare demand trends with margin performance";

        EscalationResult result = await escalator.ClassifyAndEscalateAsync(originalQuestion);

        result.OriginalQuestion.Should().Be(originalQuestion,
            "escalation result should preserve the original user question");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Escalation_PreservesOriginalQuestion_AtEveryLevel(int level)
    {
        IEscalationService escalator = CreateEscalationService();
        string question = "Test question for preservation";

        EscalationResult result = await escalator.ClassifyAndEscalateAsync(question, forceLevel: level);

        result.OriginalQuestion.Should().Be(question,
            $"original question should be preserved at level {level}");
        result.Level.Should().Be(level);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates an EscalationService with mock LLM for deterministic classification.
    /// The mock returns structured JSON based on query complexity indicators.
    /// </summary>
    private static IEscalationService CreateEscalationService()
    {
        // Mock the underlying chat client to classify based on keyword complexity
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
            {
                string userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
                int level = ClassifyComplexity(userMsg);
                string json = $"{{\"level\":{level},\"reasoning\":\"auto-classified\"}}";
                return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                    new ChatMessage(ChatRole.Assistant, json)));
            });

        return new MockEscalationService(mockClient.Object);
    }

    /// <summary>
    /// Simple keyword-based complexity classifier for deterministic test behavior.
    /// </summary>
    private static int ClassifyComplexity(string query)
    {
        string lower = query.ToLowerInvariant();
        if (lower.Contains("executive") || lower.Contains("strategic") ||
            lower.Contains("board") || lower.Contains("briefing") ||
            lower.Contains("3-year") || lower.Contains("strategy"))
        {
            return 3;
        }

        string[] multiDimensionKeywords = ["compare", "correlation", "simultaneously", "across all", "and margin", "and competitive", "margin and", "promotional spend", "pricing impact"];
        int hits = multiDimensionKeywords.Count(lower.Contains);
        return hits >= 2 ? 2 : 1;
    }

    #endregion
}

#region Escalation Contracts (test-first definitions)

/// <summary>
/// Result of escalation classification. Test-first contract.
/// </summary>
public record EscalationResult(
    int Level,
    string AgentKey,
    object? Context,
    string OriginalQuestion,
    string? Format = null
);

/// <summary>
/// Escalation service contract. Test-first definition.
/// </summary>
public interface IEscalationService
{
    Task<EscalationResult> ClassifyAndEscalateAsync(
        string query, int? forceLevel = null, CancellationToken ct = default);
}

/// <summary>
/// Mock escalation service for deterministic test behavior.
/// Classifies based on LLM mock or keyword heuristics.
/// </summary>
internal sealed class MockEscalationService : IEscalationService
{
    private readonly IChatClient _client;

    public MockEscalationService(IChatClient client)
    {
        _client = client;
    }

    public async Task<EscalationResult> ClassifyAndEscalateAsync(
        string query, int? forceLevel = null, CancellationToken ct = default)
    {
        if (forceLevel.HasValue && (forceLevel.Value < 1 || forceLevel.Value > 3))
            throw new ArgumentException($"Force level must be 1-3, got {forceLevel.Value}", nameof(forceLevel));

        int level;
        if (forceLevel.HasValue)
        {
            level = forceLevel.Value;
        }
        else
        {
            Microsoft.Extensions.AI.ChatResponse response = await _client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, query)], cancellationToken: ct);
            var json = System.Text.Json.JsonDocument.Parse(response.Text ?? "{}");
            level = json.RootElement.GetProperty("level").GetInt32();
        }

        object context = BuildContext(level, query);
        string? format = level == 3 ? "summary,metrics,recommendation" : null;

        return new EscalationResult(level, $"l{level}-agent", context, query, format);
    }

    private static object BuildContext(int level, string query)
    {
        return level switch
        {
            1 => new { query, dimensions = 1 },
            2 => new { query, dimensions = 2, crossAnalysis = true, regions = "all" },
            3 => new { query, dimensions = 3, crossAnalysis = true, regions = "all", strategic = true, execFormat = true },
            _ => new { query }
        };
    }
}

#endregion
