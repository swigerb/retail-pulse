using FluentAssertions;
using RetailPulse.Api.Agents;
using Xunit;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// The G2 acceptance contract forbids chart JSON reaching the chat bubble.
/// <see cref="AgentExecutionPipeline.ExtractInlineCharts"/> strips the blocks it can
/// bind to a ChartSpec, but a near-miss shape slips through — observed live on
/// "Compare Foundry Home vs Urban Living performance in the West Coast":
///
/// <code>
/// ```json
/// {"chart":"groupedBar","title":"West Coast Demand Comparison: Foundry Home vs Urban Living"}
/// ```
/// </code>
///
/// The key is <c>chart</c>, not <c>type</c>, so it never binds and was left in the prose.
/// </summary>
public sealed class ResidualChartJsonScrubTests
{
    [Fact]
    public void StripsTheObservedLiveLeak()
    {
        const string reply = """
            Foundry Home leads Urban Living on the West Coast.

            ```json
            {"chart":"groupedBar","title":"West Coast Demand Comparison: Foundry Home vs Urban Living"}
            ```

            Let me know if you want a regional split.
            """;

        string cleaned = AgentExecutionPipeline.StripResidualChartJson(reply);

        cleaned.Should().NotContain("```");
        cleaned.Should().NotContain("groupedBar");
        cleaned.Should().Contain("Foundry Home leads Urban Living");
        cleaned.Should().Contain("regional split");
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"bar","title":"x"}""")]
    [InlineData(/*lang=json,strict*/ """{"chart":"pie"}""")]
    [InlineData(/*lang=json,strict*/ """{"xAxisTitle":"Region","yAxisTitle":"Volume"}""")]
    [InlineData(/*lang=json,strict*/ """{"series":[{"name":"A"}]}""")]
    [InlineData(/*lang=json,strict*/ """{"legend":"Brand"}""")]
    public void StripsAnyFencedBlockCarryingChartScaffolding(string body)
    {
        string reply = "Prose before.\n\n```json\n" + body + "\n```\n\nProse after.";

        string cleaned = AgentExecutionPipeline.StripResidualChartJson(reply);

        cleaned.Should().NotContain("```");
        cleaned.Should().Contain("Prose before.");
        cleaned.Should().Contain("Prose after.");
    }

    [Fact]
    public void LeavesLegitimateJsonAlone()
    {
        // Not chart scaffolding — the user may genuinely have asked for this.
        const string reply = """
            Here is the payload you asked for.

            ```json
            {"brand":"FreshMart","region":"Northeast","units":1200}
            ```
            """;

        string cleaned = AgentExecutionPipeline.StripResidualChartJson(reply);

        cleaned.Should().Contain("```json");
        cleaned.Should().Contain("FreshMart");
    }

    [Fact]
    public void LeavesMalformedJsonAlone()
    {
        // Unparseable content is not silently hidden — same principle the inline
        // extractor already follows.
        const string reply = "Text.\n\n```json\n{\"chart\": broken\n```";

        string cleaned = AgentExecutionPipeline.StripResidualChartJson(reply);

        cleaned.Should().Contain("broken");
    }

    [Fact]
    public void IsANoOpWhenThereAreNoFences()
    {
        const string reply = "Just prose, no code fences at all.";
        AgentExecutionPipeline.StripResidualChartJson(reply).Should().Be(reply);
    }
}
