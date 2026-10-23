using FluentAssertions;
using RetailPulse.Api.Models;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Pins the kroger-retail pack's specific contract (issue #323): it loads,
/// ships the three required starting-task types, carries the disclosure, and
/// maps every router intent to a specialist.
/// </summary>
public sealed class KrogerRetailPackContractTests
{
    private static readonly string[] RouterIntents =
    [
        "demand/forecasting", "promotion/trade", "supply/shipments", "competitive/market",
        "sentiment/field", "memory/management", "council/health", "store/operations",
        "planogram/optimization", "margin/analysis", "scorecard/portfolio", "general/fallback",
    ];

    private static LoadedPack Load()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        return loader.Load("kroger-retail");
    }

    [Fact]
    public void Pack_IsDiscoveredAndLoads()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        loader.DiscoverPacks().Should().Contain("kroger-retail");

        LoadedPack pack = Load();
        pack.Name.Should().Be("kroger-retail");
        pack.StartingTasks.Should().NotBeEmpty();
        pack.Agents.Agents.Should().NotBeEmpty();
    }

    [Fact]
    public void StartingTasks_IncludeInsightCacheBypassAndApproval()
    {
        List<PackStartingTask> tasks = [.. Load().StartingTasks.SelectMany(c => c.Tasks)];

        tasks.Should().Contain(t => t.Capability != null
            && t.Capability.Kind.Equals("prose", StringComparison.OrdinalIgnoreCase),
            "a normal prose insight task is required");
        tasks.Should().Contain(t => t.Prompt.Contains("cache", StringComparison.OrdinalIgnoreCase)
            && t.Prompt.Contains("bypass", StringComparison.OrdinalIgnoreCase),
            "a gateway cache-bypass decision task is required");
        tasks.Should().Contain(t => t.Prompt.Contains("approval", StringComparison.OrdinalIgnoreCase),
            "a human-approval task is required");
    }

    [Fact]
    public void Metadata_CarriesDisclosure()
    {
        string attribution = Load().Metadata.Attribution;

        attribution.Should().ContainEquivalentOf("unofficial");
        attribution.Should().ContainEquivalentOf("no affiliation");
        attribution.Should().ContainEquivalentOf("synthetic");
        attribution.Should().ContainEquivalentOf("trademarks");
    }

    [Fact]
    public void EveryRouterIntent_IsMappedToASpecialist()
    {
        var mapped = Load().Agents.Agents.Values
            .SelectMany(a => a.Intents)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        mapped.Should().Contain(RouterIntents);
    }

    [Fact]
    public void Pack_DoesNotUseOwnedBrandLikeName()
    {
        Load().Tenant.Brands.Select(b => b.Name)
            .Should().NotContain(n => n.Contains("Private Selection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FreshnessInstructions_PreserveRequestScope_AndLimitFixedFiltersToExactCuratedTask()
    {
        AgentDefinition definition = Load().Agents.Agents["freshness-substitutions"];
        definition.Tools.Should().Contain(["GetInventoryLevels", "GetDepletionStats"]);

        string prompt = definition.SystemPrompt;
        const string curatedHeading = "## Exact curated scenario only";
        prompt.Should().Contain(curatedHeading);
        string general = prompt[..prompt.IndexOf(curatedHeading, StringComparison.Ordinal)];
        string curated = prompt[prompt.IndexOf(curatedHeading, StringComparison.Ordinal)..];

        // The loaded instructions, not scripted tool calls, protect request-scope guidance.
        string normalized = string.Join(" ", general.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized.Should().Contain("Preserve the user's brand, region, category, days-of-supply threshold, and supported period");
        normalized.Should().Contain("never replace them with the curated example's filters");
        normalized.Should().Contain("apply the requested threshold and comparison");
        normalized.Should().Contain("GetDepletionStats with the requested brand, region, and supported period");
        normalized.Should().Contain("If no period is specified, use the tool's YTD default and disclose that default");
        normalized.Should().Contain("year-over-year comparison for the selected period, not a period the user requested");
        normalized.Should().Contain("explain the limitation rather than silently changing scope");
        normalized.Should().Contain("GetHistoricalDemand returns weekly units and volume, not sell-through");
        normalized.Should().Contain("substitute another brand or region, or broaden the period when a result is empty");
        normalized.Should().Contain("no matching rows or a tool returns an error");
        normalized.Should().Contain("withhold conclusions that require missing data");
        general.Should().NotContain("brand=\"Fresh Produce\"");
        general.Should().NotContain("region=\"Heartland\"");
        general.Should().NotContain("category=\"Produce\"");
        general.Should().NotContain("days_of_supply < 2");
        general.Should().NotContain("period=\"YTD\"");

        string task = Load().StartingTasks.SelectMany(category => category.Tasks)
            .Single(task => task.Name == "Produce days of supply").Prompt;
        curated.Should().Contain($"\"{task}\"");
        curated.Should().Contain("only to this starting task");
        curated.Should().Contain("brand=\"Fresh Produce\", region=\"Heartland\"");
        curated.Should().Contain("category=\"Produce\"");
        curated.Should().Contain("days_of_supply < 2");
        curated.Should().Contain("period=\"YTD\" because this task specifies no period");
        string normalizedCurated = string.Join(" ", curated.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalizedCurated.Should().Contain("default YTD year-over-year sell-through comparison, not a user-requested timeframe");
        normalizedCurated.Should().Contain("do not override any different brand, region, category, threshold, or period");
    }
}
