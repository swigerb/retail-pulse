using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Budget;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Publix production sweep #76 Group C — every entity the user named must
/// appear on the emitted chart as a legend, category, or in the title.
/// Prod failed for prompt #19 (line: "Sierra Gold Tequila" absent from
/// legend), #23 (donut: "Apex Grill" absent) and #25 (table: "Pinnacle
/// Hardware" / "Summit Outdoor" absent from row keys).
///
/// The pipeline runs a tenant-generic post-fulfillment pass: it derives
/// requested entities from the user message by substring-matching against
/// the tenant roster (no brand literals in code), then either projects the
/// missing label losslessly (single-series chart whose title already carries
/// the brand) or drops the chart in favour of a chart-unavailable
/// diagnostic. This test pins that contract.
/// </summary>
public sealed class RequiredEntityLabelProjectionTests
{
    private static AgentExecutionPipeline BuildPipeline(TenantConfiguration tenant)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var hubContext = new Mock<IHubContext<TelemetryHub>>();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        return new AgentExecutionPipeline(
            Mock.Of<IChatClient>(),
            hubContext.Object,
            streamingHubContext: null,
            streamingFeature: null,
            config,
            NullLoggerFactory.Instance.CreateLogger<AgentExecutionPipeline>(),
            metrics: null,
            anonymousChatPolicy: NoOpAnonymousChatPolicy.Instance,
            tenant: tenant);
    }

    private static TenantConfiguration TenantWith(params string[] brands) => new()
    {
        BrandsList = [.. brands.Select(b => new BrandConfig { Name = b, Category = "Cat" })],
        RegionsList = ["Northeast", "Midwest"],
    };

    [Fact]
    public void SingleSeriesChart_WithBrandInTitle_IsAcceptedUnchanged()
    {
        // Contract mirrors the ChartAcceptanceManifest matrix — a required
        // entity may appear as a legend, a category, OR in the title. When
        // it's in the title the chart is left unchanged.
        AgentExecutionPipeline pipeline = BuildPipeline(TenantWith("Sierra Gold Tequila"));
        var chart = new ChartSpec
        {
            Type = "line",
            Title = "Sierra Gold Tequila Depletion Trends",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Depletions",
                    Values =
                    [
                        new ChartDataPoint { X = "Northeast", Y = 100 },
                        new ChartDataPoint { X = "Midwest",   Y = 120 },
                    ],
                },
            ],
        };
        var response = new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "prose"));

        AgentExecutionPipeline.ChartLabelProjectionResult result = pipeline.EnforceRequiredEntityLabels(
            "Create a line chart showing Sierra Gold Tequila depletion trends across all regions",
            response,
            [chart],
            "Here is the chart.");

        result.Charts.Should().HaveCount(1);
        result.Reply.Should().Be("Here is the chart.");
    }

    [Fact]
    public void SingleSeriesChart_WithBrandInToolPayloadOnly_ProjectsBrandOntoLegend()
    {
        // The chart itself carries neither the brand in its title nor in a
        // legend or category (the model narrated only "Depletion Trends"),
        // BUT the underlying tool result was scoped to that brand. The
        // projection pass must promote the brand from the tool payload onto
        // the single series' legend so the chart survives label enforcement.
        AgentExecutionPipeline pipeline = BuildPipeline(TenantWith("Sierra Gold Tequila"));
        var chart = new ChartSpec
        {
            Type = "line",
            Title = "Depletion Trends",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Depletions",
                    Values =
                    [
                        new ChartDataPoint { X = "Northeast", Y = 100 },
                        new ChartDataPoint { X = "Midwest",   Y = 120 },
                    ],
                },
            ],
        };

        // Simulate a tool result that carries the brand filter.
        var toolResult = new FunctionResultContent(
            "call-1",
            /*lang=json,strict*/ """{"filters":{"brand":"Sierra Gold Tequila"},"weekly_data":[]}""");
        var response = new MeaiChatResponse(
            new ChatMessage(ChatRole.Assistant, [toolResult]));

        AgentExecutionPipeline.ChartLabelProjectionResult result = pipeline.EnforceRequiredEntityLabels(
            "Create a line chart showing Sierra Gold Tequila depletion trends across all regions",
            response,
            [chart],
            "Here is the chart.");

        result.Charts.Should().HaveCount(1);
        ChartSpec projected = result.Charts[0];
        (projected.Data[0].Legend?.Contains("Sierra Gold Tequila", StringComparison.OrdinalIgnoreCase) ?? false)
            .Should().BeTrue(
                "the projection pass must promote the requested brand from the tool payload onto the series legend");
    }

    [Fact]
    public void MultiSeriesChart_DroppingRequiredEntity_IsRemovedAndDiagnosticEmitted()
    {
        AgentExecutionPipeline pipeline = BuildPipeline(TenantWith("Pinnacle Hardware", "Summit Outdoor"));
        // A table-shaped chart with two rows keyed by generic categories that
        // do NOT mention the required brands — a silently mis-labelled table.
        var chart = new ChartSpec
        {
            Type = "table",
            Title = "Depletion Stats",
            Data =
            [
                new ChartSeries
                {
                    Legend = "Depletions",
                    Values =
                    [
                        new ChartDataPoint { X = "Row 1", Y = 3.2 },
                        new ChartDataPoint { X = "Row 2", Y = 4.1 },
                    ],
                },
            ],
        };
        var response = new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "prose"));

        AgentExecutionPipeline.ChartLabelProjectionResult result = pipeline.EnforceRequiredEntityLabels(
            "Create a table showing depletion stats for all home improvement brands by region including Pinnacle Hardware and Summit Outdoor",
            response,
            [chart],
            "Here is the table.");

        result.Charts.Should().BeEmpty(
            "a table missing required brand row-keys must be dropped rather than silently shipped");
        result.Reply.Should().Contain("Chart unavailable",
            "the diagnostic must inform the user why the chart was not shown");
        result.Reply.Should().Contain("Pinnacle Hardware");
        result.Reply.Should().Contain("Summit Outdoor");
    }

    [Fact]
    public void PromptWithNoRosterEntity_IsNoOp()
    {
        AgentExecutionPipeline pipeline = BuildPipeline(TenantWith("BrandA", "BrandB"));
        var chart = new ChartSpec
        {
            Type = "bar",
            Title = "Some chart",
            Data =
            [
                new ChartSeries
                {
                    Legend = "L",
                    Values = [new ChartDataPoint { X = "x1", Y = 1 }, new ChartDataPoint { X = "x2", Y = 2 }],
                },
            ],
        };
        var response = new MeaiChatResponse(new ChatMessage(ChatRole.Assistant, "prose"));

        AgentExecutionPipeline.ChartLabelProjectionResult result = pipeline.EnforceRequiredEntityLabels(
            "Show me some totals for this quarter",
            response,
            [chart],
            "Here it is.");

        result.Charts.Should().HaveCount(1);
        result.Reply.Should().Be("Here it is.");
    }
}
