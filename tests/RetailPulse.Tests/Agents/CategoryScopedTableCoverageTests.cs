using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Agents;
using RetailPulse.Api.Charts;
using RetailPulse.Contracts;
using RetailPulse.Tests.Fixtures;
using Xunit;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;

namespace RetailPulse.Tests.Agents;

/// <summary>
/// Publix production sweep #76 — extend the tenant-generic roster-coverage
/// invariant (issue #74) to CATEGORY-SCOPED chart requests and to the table
/// chart type. Prompt #25 ("Create a table showing depletion stats for all
/// home improvement brands by region") emitted a table that rendered ONLY
/// Pinnacle Hardware — Summit Outdoor was silently dropped from series and
/// legend. The coverage guard only fired for the horizontal-bar portfolio path;
/// this suite pins that:
/// <list type="bullet">
///   <item>a category-scoped table with the complete category roster is accepted;</item>
///   <item>a category-scoped table that drops any category brand is REJECTED and
///     the pipeline rebuilds a roster-complete table from tool results, or fails
///     closed with a diagnostic listing the missing brands;</item>
///   <item>the roster is derived from tenant.yaml (count and identity — no
///     hardcoded brand or category literals in the enforcement).</item>
/// </list>
/// </summary>
public sealed class CategoryScopedTableCoverageTests
{
    private const string HomeImprovementTablePrompt =
        "Create a table showing depletion stats for all home improvement brands by region";

    // ── Coverage acceptance ─────────────────────────────────────────────────

    [Fact]
    public void EnforceChartFulfillment_TableCoveringAllCategoryBrands_IsAccepted()
    {
        AgentExecutionPipeline pipeline = CreatePipelineWithHomeImprovementTenant();
        MeaiChatResponse response = ResponseWithToolResults(
            HomeImprovementPayload(
                ("Pinnacle Hardware", "Northeast", 3.1),
                ("Pinnacle Hardware", "Southeast", 2.4),
                ("Summit Outdoor", "Northeast", 4.2),
                ("Summit Outdoor", "Southeast", 5.0)));

        var charts = new List<ChartSpec>
        {
            new()
            {
                Type = "table",
                Title = "Depletion Stats by Region",
                XAxisTitle = "Brand / Region",
                YAxisTitle = "Depletion Stats",
                Data =
                [
                    new ChartSeries
                    {
                        Legend = "Depletions YoY %",
                        Values =
                        [
                            new ChartDataPoint { X = "Pinnacle Hardware — Northeast", Y = 3.1 },
                            new ChartDataPoint { X = "Pinnacle Hardware — Southeast", Y = 2.4 },
                            new ChartDataPoint { X = "Summit Outdoor — Northeast", Y = 4.2 },
                            new ChartDataPoint { X = "Summit Outdoor — Southeast", Y = 5.0 },
                        ],
                    },
                ],
            },
        };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            HomeImprovementTablePrompt, response, charts, "Here is the table.");

        result.Charts.Should().ContainSingle();
        result.Charts[0].Type.Should().Be("table");
        // Both category brands must be represented (roster-complete).
        string flattened = string.Join(",", result.Charts[0].Data.SelectMany(s => s.Values).Select(v => v.X));
        flattened.Should().Contain("Pinnacle Hardware");
        flattened.Should().Contain("Summit Outdoor");
    }

    // ── Coverage rejection: silently-dropped brand ──────────────────────────

    [Fact]
    public void EnforceChartFulfillment_TableMissingCategoryBrand_ReplacedOrFailsClosed()
    {
        AgentExecutionPipeline pipeline = CreatePipelineWithHomeImprovementTenant();

        // Tool payload includes BOTH brands — so the deterministic rebuilder must
        // succeed and the pipeline must replace the partial model chart with the
        // roster-complete table.
        MeaiChatResponse response = ResponseWithToolResults(
            HomeImprovementPayload(
                ("Pinnacle Hardware", "Northeast", 3.1),
                ("Pinnacle Hardware", "Southeast", 2.4),
                ("Summit Outdoor", "Northeast", 4.2),
                ("Summit Outdoor", "Southeast", 5.0)));

        // Model-emitted chart drops Summit Outdoor entirely — the exact #25 failure.
        var charts = new List<ChartSpec>
        {
            new()
            {
                Type = "table",
                Title = "Depletion Stats — Home Improvement",
                XAxisTitle = "Brand / Region",
                YAxisTitle = "Depletion Stats",
                Data =
                [
                    new ChartSeries
                    {
                        Legend = "Depletions YoY %",
                        Values =
                        [
                            new ChartDataPoint { X = "Pinnacle Hardware — Northeast", Y = 3.1 },
                            new ChartDataPoint { X = "Pinnacle Hardware — Southeast", Y = 2.4 },
                        ],
                    },
                ],
            },
        };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            HomeImprovementTablePrompt, response, charts, "Here is the table.");

        // Either the pipeline rebuilt a roster-complete table, or it failed closed.
        // Both branches must NEVER leave a chart that silently drops Summit Outdoor.
        if (result.Charts.Count > 0)
        {
            ChartSpec c = result.Charts[0];
            c.Type.Should().Be("table");
            string flattened = string.Join(",", c.Data.SelectMany(s => s.Values).Select(v => v.X));
            flattened.Should().Contain("Summit Outdoor",
                "the roster-complete rebuild must include every configured tenant brand in the requested category");
            flattened.Should().Contain("Pinnacle Hardware");
        }
        else
        {
            result.Reply.Should().Contain("Chart unavailable",
                "when the rebuild is impossible the diagnostic must fail closed rather than emit a partial table");
            result.Reply.Should().Contain("Summit Outdoor",
                "the fail-closed diagnostic must list which brand(s) were dropped by the model");
        }
    }

    // ── Roster derivation: tenant-generic, no literals ──────────────────────

    [Fact]
    public void CoverageRoster_IsDerivedFromTenantYaml_CountAndIdentity()
    {
        // Change the tenant roster: two extra Home Improvement brands added.
        // The coverage guard must adapt — count and identity of the required
        // roster come entirely from tenant.yaml.
        var tenant = new TenantConfiguration
        {
            Company = "TestCo",
            BrandsList =
            [
                new BrandConfig { Name = "Alpha Hardware", Category = "Home Improvement" },
                new BrandConfig { Name = "Beta Hardware", Category = "Home Improvement" },
                new BrandConfig { Name = "Gamma Hardware", Category = "Home Improvement" },
                new BrandConfig { Name = "Unrelated Spirit", Category = "Spirits" },
            ],
            RegionsList = ["Northeast", "Southeast"],
        };

        AgentExecutionPipeline pipeline = CreatePipeline(tenant);
        MeaiChatResponse response = ResponseWithToolResults(
            HomeImprovementPayload(
                ("Alpha Hardware", "Northeast", 1.0),
                ("Alpha Hardware", "Southeast", 1.0),
                ("Beta Hardware", "Northeast", 1.0),
                ("Beta Hardware", "Southeast", 1.0),
                ("Gamma Hardware", "Northeast", 1.0),
                ("Gamma Hardware", "Southeast", 1.0)));

        // Model chart only covers Alpha — must be replaced or fail closed for
        // both Beta AND Gamma, proving the roster is derived from tenant.yaml.
        var charts = new List<ChartSpec>
        {
            new()
            {
                Type = "table",
                Title = "Depletion Stats",
                XAxisTitle = "Brand / Region",
                YAxisTitle = "Depletion Stats",
                Data =
                [
                    new ChartSeries
                    {
                        Legend = "Depletions YoY %",
                        Values =
                        [
                            new ChartDataPoint { X = "Alpha Hardware — Northeast", Y = 1.0 },
                            new ChartDataPoint { X = "Alpha Hardware — Southeast", Y = 1.0 },
                        ],
                    },
                ],
            },
        };

        AgentExecutionPipeline.ChartFulfillmentResult result = pipeline.EnforceChartFulfillment(
            HomeImprovementTablePrompt, response, charts, "Table.");

        // Either the rebuild produced a table covering every home-improvement
        // brand (3 brands × 2 regions = 6 rows) or the pipeline failed closed
        // and named the missing brands.
        if (result.Charts.Count > 0)
        {
            string flattened = string.Join(",", result.Charts[0].Data.SelectMany(s => s.Values).Select(v => v.X));
            flattened.Should().Contain("Alpha Hardware");
            flattened.Should().Contain("Beta Hardware");
            flattened.Should().Contain("Gamma Hardware");
            flattened.Should().NotContain("Unrelated Spirit",
                "category scope is derived from tenant.yaml — brands outside the category must not sneak in");
        }
        else
        {
            result.Reply.Should().Contain("Beta Hardware");
            result.Reply.Should().Contain("Gamma Hardware");
            result.Reply.Should().NotContain("Unrelated Spirit");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentExecutionPipeline CreatePipelineWithHomeImprovementTenant()
    {
        var tenant = new TenantConfiguration
        {
            Company = "TestCo",
            BrandsList =
            [
                new BrandConfig { Name = "Pinnacle Hardware", Category = "Home Improvement" },
                new BrandConfig { Name = "Summit Outdoor", Category = "Home Improvement" },
                new BrandConfig { Name = "Unrelated Grocery", Category = "Grocery" },
            ],
            RegionsList = ["Northeast", "Southeast"],
        };
        return CreatePipeline(tenant);
    }

    private static AgentExecutionPipeline CreatePipeline(TenantConfiguration tenant)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new AgentExecutionPipeline(
            AgentTestFixtures.CreateMockChatClient("{}"),
            hubContext: AgentTestFixtures.CreateMockHubContext(),
            streamingHubContext: null,
            streamingFeature: null,
            configuration: config,
            logger: NullLogger<AgentExecutionPipeline>.Instance,
            metrics: null,
            anonymousChatPolicy: Api.Auth.NoOpAnonymousChatPolicy.Instance,
            tenant: tenant);
    }

    private static string HomeImprovementPayload(params (string Brand, string Region, double DepletionsYoy)[] rows)
    {
        var brands = rows.Select(r => new
        {
            brand = r.Brand,
            region = r.Region,
            metrics = new
            {
                depletions_yoy = (r.DepletionsYoy >= 0 ? "+" : "") + r.DepletionsYoy.ToString("0.0") + "%",
                sell_through_yoy = "+1.0%",
                inventory_weeks_on_hand = 6.0,
                status = "OnTrack",
            },
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            region = "AllRegions",
            regions = rows.Select(r => r.Region).Distinct().ToArray(),
            period = "YTD",
            brandCount = rows.Select(r => r.Brand).Distinct().Count(),
            category = "Home Improvement",
            brands,
        });
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
