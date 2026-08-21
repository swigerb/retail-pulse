using FluentAssertions;
using RetailPulse.Api.Packs;

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
        PackLoader loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);

        LoadedPack pack = loader.Load(packName);

        pack.Name.Should().Be(packName);
        pack.Metadata.Key.Should().Be(packName);
        pack.Agents.Agents.Should().NotBeEmpty($"pack '{packName}' should ship at least one agent");
        pack.Tenant.Company.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ShippedPacks_IncludeDefaultAndTwoFictionalExamples()
    {
        PackLoader loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);

        IReadOnlyList<string> discovered = loader.DiscoverPacks();

        discovered.Should().Contain("default");
        discovered.Where(p => p != "default").Should().HaveCountGreaterThanOrEqualTo(2,
            "issue #108 requires at least two additional fictional example packs alongside default");
    }
}
