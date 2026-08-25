using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Contract coverage for issue #76 Groups A + D on both the fast path (raw user
/// message hits <see cref="AgentExecutionPipeline.EnforceChartFulfillment"/> directly)
/// and the plan path (the specialist step receives the plan's scoped message
/// <c>"{action} — original user request: {message}"</c>, which is what the
/// <c>PlanExecutor</c> builds before delegating to the same pipeline).
/// <para>
/// The two failure classes pinned here are:
/// </para>
/// <list type="bullet">
///   <item><b>Group A — chart on prose.</b> A prose prompt where the model emits a
///     chart must have that chart dropped before the response leaves the pipeline.
///     No prose intent carries a chart exception (design decision, PR body).</item>
///   <item><b>Group D — chart-type family guard.</b> When the request captured a
///     specific chart type, a within-family mismatch (bar → horizontalBar) may be
///     coerced deterministically because the data shape is identical; a cross-family
///     mismatch (bar → line, or bar → gauge) must fail closed with a chart-type
///     mismatch diagnostic — never a silent rewrite.</item>
/// </list>
/// Both defects would have shipped without these tests; the fast/plan pairs prove
/// the invariant applies transitively through the plan-first architecture because
/// <c>PlanExecutor</c> injects the raw user message into every step's scoped message.
/// </summary>
public sealed class Issue76ChartGuardsTests
{
    // ── GROUP A: chart-on-prose (fast path + plan path) ─────────────────────

    [Theory]
    [InlineData("How is the portfolio performing?")]
    [InlineData("Summarize distributor sentiment this quarter.")]
    [InlineData("What's the inventory level for Pinnacle Hardware?")]
    public void FastPath_ProsePrompt_ModelEmittedChart_IsDropped(string prosePrompt)
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "prose payload — no chart requested" }));

        // Model hallucinated a chart even though the user asked for prose.
        var stray = new ChartSpec
        {
            Type = "bar",
            Title = "Unrequested",
            Data = [new ChartSeries { Legend = "s", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };
        var charts = new List<ChartSpec> { stray };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            prosePrompt, response, charts, "Here is the prose summary the user asked for.");

        result.Charts.Should().BeEmpty(
            "prose intents must never surface a model-emitted chart — chart-on-prose invariant, issue #76 Group A");
        result.Reply.Should().Be("Here is the prose summary the user asked for.",
            "the prose reply must survive the drop untouched");
    }

    [Theory]
    [InlineData("How is the portfolio performing?")]
    [InlineData("Summarize distributor sentiment this quarter.")]
    public void PlanPath_ProsePrompt_ModelEmittedChart_IsDropped(string prosePrompt)
    {
        // Plan-path shape: PlanExecutor scopes the step message as
        //   "{action} — original user request: {message}"
        // ChartRequestDetector sees the full concatenation. If the original user
        // request is prose and the step action has no chart nouns, the invariant
        // must still drop the model-emitted chart at the specialist boundary.
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "prose payload — no chart requested" }));

        string stepMessage =
            $"aggregate cross-brand depletion metrics — original user request: {prosePrompt}";

        var stray = new ChartSpec
        {
            Type = "bar",
            Title = "Unrequested",
            Data = [new ChartSeries { Legend = "s", Values = [new ChartDataPoint { X = "A", Y = 1 }] }]
        };
        var charts = new List<ChartSpec> { stray };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            stepMessage, response, charts, "The prose synthesis for the plan step.");

        result.Charts.Should().BeEmpty(
            "plan-path prose prompts must drop model-emitted charts identically to the fast path");
        result.Reply.Should().Be("The prose synthesis for the plan step.");
    }

    // ── GROUP D: within-family coercion (fast path + plan path) ─────────────

    public static IEnumerable<object[]> WithinFamilyCases =>
    [
        // Requested → emitted, expected coerced-to.
        ["Show me a bar chart of depletion velocity in the Northeast",
            "bar", "horizontalBar", "bar"],
        ["Show me a horizontal bar chart ranking brands by depletion",
            "horizontalBar", "bar", "horizontalBar"],
        ["Show me a grouped bar chart of depletion by region",
            "groupedBar", "stackedBar", "groupedBar"],
        ["Show me a donut chart of market share by competitor",
            "donut", "pie", "donut"],
    ];

    [Theory]
    [MemberData(nameof(WithinFamilyCases))]
    public void FastPath_WithinFamilyMismatch_IsCoerced(
        string prompt, string requested, string modelEmitted, string coercedTo)
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "chart data payload" }));

        var emitted = new ChartSpec
        {
            Type = modelEmitted,
            Title = $"{modelEmitted} sample",
            Data =
            [
                new ChartSeries
                {
                    Legend = "series",
                    Values = [new ChartDataPoint { X = "A", Y = 1 }, new ChartDataPoint { X = "B", Y = 2 }]
                }
            ]
        };
        var charts = new List<ChartSpec> { emitted };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            prompt, response, charts, "Here is the chart.");

        result.Charts.Should().ContainSingle(
            "within-family coercion must never drop the chart — the data shape binds");
        result.Charts[0].Type.Should().Be(coercedTo,
            $"the model emitted '{modelEmitted}' but the user asked for '{requested}' — same family, coerce in place");
        // Coercion preserves data verbatim.
        result.Charts[0].Data[0].Values.Should().HaveCount(2);
        result.Reply.Should().NotContain("Chart unavailable",
            "within-family coercion is a rendering-orientation fix, not a fail-closed path — issue #76 requires prompt = '{0}'", prompt);
    }

    [Theory]
    [MemberData(nameof(WithinFamilyCases))]
    public void PlanPath_WithinFamilyMismatch_IsCoerced(
        string prompt, string requested, string modelEmitted, string coercedTo)
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "chart data payload" }));

        string stepMessage = $"visualize brand comparison — original user request: {prompt}";

        var emitted = new ChartSpec
        {
            Type = modelEmitted,
            Title = $"{modelEmitted} sample",
            Data =
            [
                new ChartSeries
                {
                    Legend = "series",
                    Values = [new ChartDataPoint { X = "A", Y = 1 }, new ChartDataPoint { X = "B", Y = 2 }]
                }
            ]
        };
        var charts = new List<ChartSpec> { emitted };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            stepMessage, response, charts, "Here is the chart.");

        // Note: the "horizontalBar ranking brands" prompt is a portfolio-ranking
        // intent — without a tenant roster the coverage invariant is bypassed and
        // the coerced chart survives. See CreatePipeline() which supplies no
        // roster, so this fast/plan pair is symmetric.
        _ = requested;
        result.Charts.Should().ContainSingle(
            "plan-path within-family coercion must work identically to the fast path");
        result.Charts[0].Type.Should().Be(coercedTo);
    }

    // ── GROUP D: cross-family error (fast path + plan path) ─────────────────

    public static IEnumerable<object[]> CrossFamilyCases =>
    [
        // Requested → emitted (different structural family).
        ["Show me a bar chart of depletion velocity in the Northeast", "bar", "line"],
        ["Show me a line chart of margin trend by quarter", "line", "bar"],
        ["Show a pie chart of market share by competitor", "pie", "line"],
        ["Show me a bar chart of depletion velocity in the Northeast", "bar", "gauge"],
        ["Show me a bar chart of depletion velocity in the Northeast", "bar", "table"],
    ];

    [Theory]
    [MemberData(nameof(CrossFamilyCases))]
    public void FastPath_CrossFamilyMismatch_IsExplicitError(
        string prompt, string requested, string modelEmitted)
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "chart data payload" }));

        var emitted = new ChartSpec
        {
            Type = modelEmitted,
            Title = $"{modelEmitted} sample",
            Data =
            [
                new ChartSeries
                {
                    Legend = "series",
                    Values = [new ChartDataPoint { X = "A", Y = 1 }, new ChartDataPoint { X = "B", Y = 2 }]
                }
            ]
        };
        var charts = new List<ChartSpec> { emitted };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            prompt, response, charts, "Here is the chart.");

        result.Charts.Should().BeEmpty(
            $"a cross-family mismatch ({modelEmitted} → {requested}) must fail closed, not silently rewrite");
        result.Reply.Should().Contain("Chart unavailable",
            "cross-family mismatches must surface a structured diagnostic to the user");
        result.Reply.Should().Contain(requested);
        result.Reply.Should().Contain(modelEmitted);
    }

    [Theory]
    [MemberData(nameof(CrossFamilyCases))]
    public void PlanPath_CrossFamilyMismatch_IsExplicitError(
        string prompt, string requested, string modelEmitted)
    {
        AgentExecutionPipeline pipeline = CreatePipeline();
        MeaiChatResponse response = ResponseWithToolResults(
            JsonSerializer.Serialize(new { note = "chart data payload" }));

        string stepMessage = $"visualize the requested comparison — original user request: {prompt}";

        var emitted = new ChartSpec
        {
            Type = modelEmitted,
            Title = $"{modelEmitted} sample",
            Data =
            [
                new ChartSeries
                {
                    Legend = "series",
                    Values = [new ChartDataPoint { X = "A", Y = 1 }, new ChartDataPoint { X = "B", Y = 2 }]
                }
            ]
        };
        var charts = new List<ChartSpec> { emitted };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            stepMessage, response, charts, "Here is the chart.");

        result.Charts.Should().BeEmpty();
        result.Reply.Should().Contain("Chart unavailable");
        result.Reply.Should().Contain(requested);
        result.Reply.Should().Contain(modelEmitted);
        _ = requested;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentExecutionPipeline CreatePipeline()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new AgentExecutionPipeline(
            AgentTestFixtures.CreateMockChatClient("{}"),
            AgentTestFixtures.CreateMockHubContext(),
            config,
            NullLogger<AgentExecutionPipeline>.Instance);
    }

    private static MeaiChatResponse ResponseWithToolResults(params string[] toolResultJson)
    {
        var contents = new List<AIContent>();
        for (int i = 0; i < toolResultJson.Length; i++)
        {
            contents.Add(new FunctionResultContent($"call-{i}", toolResultJson[i]));
        }
        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new MeaiChatResponse(message);
    }
}
