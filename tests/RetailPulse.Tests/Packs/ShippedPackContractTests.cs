using FluentAssertions;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Every pack shipped under <c>packs/</c> must load cleanly through
/// <see cref="PackLoader"/>. This keeps the fictional example packs
/// (halcyon-pet-supply, prairiehearth-craft-supply) honest — an author
/// can't merge a broken example without the tests turning red.
/// </summary>
public sealed class ShippedPackContractTests
{
    public static IEnumerable<object[]> ShippedPacks() =>
        Directory
            .EnumerateDirectories(PackTestPaths.PacksRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => new object[] { n! });

    [Theory]
    [MemberData(nameof(ShippedPacks))]
    public void ShippedPack_LoadsWithoutStructuralIssues(string packName)
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);

        LoadedPack pack = loader.Load(packName);

        pack.Name.Should().Be(packName);
        pack.Metadata.Key.Should().Be(packName);
        pack.Agents.Agents.Should().NotBeEmpty($"pack '{packName}' should ship at least one agent");
        pack.Tenant.Company.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ShippedPacks))]
    public void ShippedPack_HasValidSeedManifest(string packName)
    {
        // Blocker #6: every shipped pack must ship a fully-populated
        // seed/scenario.yaml. The CI enumeration proves the file
        // exists, loads through SeedManifestLoader without error, and
        // populates every required section — no README-only placeholder
        // is allowed to slip through the packs directory.
        string seedDir = Path.Combine(PackTestPaths.PacksRoot, packName, "seed");

        Directory.Exists(seedDir).Should().BeTrue(
            "pack '{0}' must ship seed/", packName);

        SeedManifest seed = SeedManifestLoader.LoadFromDirectory(seedDir);

        seed.Should().NotBeNull();
        seed.Promos.Types.Should().NotBeEmpty(
            "pack '{0}' seed/scenario.yaml must declare at least one promo type", packName);
        seed.Competitive.CompetitorsByCategory.Should().NotBeEmpty(
            "pack '{0}' seed/scenario.yaml must declare competitor rosters", packName);
        seed.Supply.DisruptionTypes.Should().NotBeEmpty(
            "pack '{0}' seed/scenario.yaml must declare supply disruption types", packName);
        seed.Stores.Types.Should().NotBeEmpty(
            "pack '{0}' seed/scenario.yaml must declare store types", packName);
        seed.Margin.DriverCategories.Should().NotBeEmpty(
            "pack '{0}' seed/scenario.yaml must declare margin driver categories", packName);
    }

    [Theory]
    [MemberData(nameof(ShippedPacks))]
    public void ShippedPack_LoaderExposesSeedOnLoadedPack(string packName)
    {
        // The seed manifest must reach LoadedPack.Seed by the shared
        // PackLoader, not just SeedManifestLoader directly — this
        // guards against a regression where the loader silently
        // forgets the seed section.
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        LoadedPack pack = loader.Load(packName);

        pack.Seed.Should().NotBeNull();
        pack.Seed.Promos.Types.Should().NotBeEmpty();
    }

    [Fact]
    public void ShippedPacks_IncludeDefaultAndTwoFictionalExamples()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);

        IReadOnlyList<string> discovered = loader.DiscoverPacks();

        discovered.Should().Contain("default");
        discovered.Where(p => p != "default").Should().HaveCountGreaterThanOrEqualTo(2,
            "issue #108 requires at least two additional fictional example packs alongside default");
    }
}
