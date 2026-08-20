using FluentAssertions;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Structural invariants for the golden dataset. These do not exercise the router — they
/// keep the dataset itself honest so a future PR can't quietly delete coverage or introduce
/// a mistyped intent.
/// </summary>
public sealed class DatasetContractTests
{
    private static readonly string[] _requiredChartCategories =
    [
        "chart-line",
        "chart-bar",
        "chart-groupedBar",
        "chart-stackedBar",
        "chart-horizontalBar",
        "chart-pie",
        "chart-donut",
        "chart-gauge",
        "chart-table",
    ];

    private static readonly string[] _requiredNonChartCategories =
    [
        "fast-path-single-domain",
        "fast-path-council",
        "fast-path-domain-keyword",
        "cross-region-comparison",
        "memory-management-store",
        "memory-management-destructive",
        "adversarial-injection",
        "refusal-out-of-scope",
        "refusal-tenant-unavailable",
        "ambiguous-clarification",
        "retrieval-knowledge-grounded",
    ];

    [Fact]
    public void Dataset_Loads_And_HasRequiredMetadata()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();

        dataset.Version.Should().BeGreaterThanOrEqualTo(1);
        dataset.GeneratedAtLocal.Should().NotBeNullOrWhiteSpace();
        dataset.HarnessScope.Should().NotBeNullOrWhiteSpace();
        dataset.Cases.Should().NotBeEmpty();
    }

    [Fact]
    public void EveryCase_HasUniqueId_AndNonEmptyPrompt()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();

        IEnumerable<IGrouping<string, GoldenCase>> duplicates = dataset.Cases
            .GroupBy(c => c.Id)
            .Where(g => g.Count() > 1);
        duplicates.Should().BeEmpty("golden case ids must be unique");

        foreach (GoldenCase c in dataset.Cases)
        {
            c.Id.Should().NotBeNullOrWhiteSpace();
            c.Category.Should().NotBeNullOrWhiteSpace($"case {c.Id} missing category");
            c.Prompt.Should().NotBeNullOrWhiteSpace($"case {c.Id} missing prompt");
        }
    }

    [Fact]
    public void Dataset_Covers_AllNineChartTypes()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();

        foreach (string chartCategory in _requiredChartCategories)
        {
            dataset.Cases.Should().Contain(c => c.Category == chartCategory,
                $"chart-type coverage requires at least one case in category '{chartCategory}' — "
                + "the harness must exercise every one of the nine supported chart types");
        }
    }

    [Fact]
    public void Dataset_Covers_AllRequiredNonChartCategories()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();

        foreach (string category in _requiredNonChartCategories)
        {
            dataset.Cases.Should().Contain(c => c.Category == category,
                $"category '{category}' must have at least one representative case");
        }
    }

    [Fact]
    public void EveryCase_RoutingMode_And_RoutingIntent_AreValid()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        string[] validRoutingModes = ["keyword-fast-path", "llm-required"];

        foreach (GoldenCase c in dataset.Cases)
        {
            validRoutingModes.Should().Contain(c.Expectations.RoutingMode,
                $"case {c.Id} has unknown routing_mode '{c.Expectations.RoutingMode}'");

            if (c.Expectations.RoutingMode == "keyword-fast-path")
            {
                string? intent = c.Expectations.RoutingIntent;
                intent.Should().NotBeNull(
                    $"case {c.Id} keyword-fast-path must declare its expected routing_intent");
                AgentIntent.All.Should().Contain(intent,
                    $"case {c.Id} declares an intent that is not one of AgentIntent.All");
            }
        }
    }

    [Fact]
    public void EveryCase_ExplicitChart_Implies_ChartType()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();

        foreach (GoldenCase c in dataset.Cases)
        {
            if (c.Expectations.ExplicitChart)
            {
                c.Expectations.ChartType.Should().NotBeNullOrEmpty(
                    $"case {c.Id}: explicit_chart=true requires a chart_type");
            }
            else
            {
                c.Expectations.ChartType.Should().BeNull(
                    $"case {c.Id}: explicit_chart=false must have chart_type=null");
            }
        }
    }
}
