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
}
