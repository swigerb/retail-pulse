using FluentAssertions;
using RetailPulse.Api.Packs;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Issue #108 acceptance guarantee that a pack switch changes every user-
/// visible dimension together — a pack is not a reskin. Loads every
/// shipped pack and asserts that brands, regions, channels, theme,
/// distribution model, agent roster, knowledge corpus, and starting
/// tasks are genuinely distinct across the set.
/// </summary>
public sealed class PackSwitchDimensionsTests
{
    private static IReadOnlyList<LoadedPack> LoadAllShipped()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        return [.. loader.DiscoverPacks().Select(loader.Load)];
    }

    [Fact]
    public void PackSwitch_TenantDimensionsAreDistinctAcrossShippedPacks()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();
        packs.Should().HaveCountGreaterThanOrEqualTo(3);

        packs.Select(p => p.Tenant.Company).Should().OnlyHaveUniqueItems();
        packs.Select(p => p.Tenant.Industry).Should().OnlyHaveUniqueItems();
        packs.Select(p => p.Tenant.Theme.PrimaryColor).Should().OnlyHaveUniqueItems();
        packs.Select(p => p.Tenant.Theme.AccentColor).Should().OnlyHaveUniqueItems();
        packs.Select(p => p.Tenant.Distribution.Model).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void PackSwitch_BrandsRegionsChannelsShareNoOverlapAcrossPacks()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();

        var brandsPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.Tenant.Brands.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var regionsPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.Tenant.Regions.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var channelsPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.Tenant.Channels.ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (LoadedPack a in packs)
        {
            foreach (LoadedPack b in packs)
            {
                if (ReferenceEquals(a, b))
                {
                    continue;
                }
                brandsPerPack[a.Name].Overlaps(brandsPerPack[b.Name])
                    .Should().BeFalse("packs '{0}' and '{1}' must not share any brand names", a.Name, b.Name);
                regionsPerPack[a.Name].Overlaps(regionsPerPack[b.Name])
                    .Should().BeFalse("packs '{0}' and '{1}' must not share any region names", a.Name, b.Name);
                channelsPerPack[a.Name].Overlaps(channelsPerPack[b.Name])
                    .Should().BeFalse("packs '{0}' and '{1}' must not share any channel names", a.Name, b.Name);
            }
        }
    }

    [Fact]
    public void PackSwitch_SpecialistRosterIsGenuinelyDifferent_NotReskinned()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();

        foreach (LoadedPack pack in packs)
        {
            int specialists = pack.Agents.Agents.Count(a =>
                !string.Equals(a.Key, "router", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(a.Key, "memory-management", StringComparison.OrdinalIgnoreCase));
            specialists.Should().BeGreaterThanOrEqualTo(3,
                "pack '{0}' should ship at least three domain specialists", pack.Name);
        }

        var defaultSpecialistKeys = packs
            .Single(p => p.Name == "default")
            .Agents.Agents.Keys
            .Where(k => !string.Equals(k, "router", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(k, "memory-management", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (LoadedPack pack in packs.Where(p => p.Name != "default"))
        {
            IReadOnlyCollection<string> uniqueSpecialists = [.. pack.Agents.Agents.Keys
                .Where(k => !defaultSpecialistKeys.Contains(k)
                            && !string.Equals(k, "router", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(k, "memory-management", StringComparison.OrdinalIgnoreCase))];

            uniqueSpecialists.Should().NotBeEmpty(
                "pack '{0}' should introduce specialists not shared with default", pack.Name);
        }

        IReadOnlyCollection<string> allSpecialistDisplayNames = [.. packs
            .SelectMany(p => p.Agents.Agents.Values
                .Where(v => string.Equals(v.Role, "specialist", StringComparison.OrdinalIgnoreCase))
                .Select(v => (v.DisplayName ?? v.Name ?? "").Trim()))
            .Where(n => n.Length > 0)];

        allSpecialistDisplayNames.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void PackSwitch_KnowledgeCorpusDiffersAcrossPacks()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();

        var sourcesPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.KnowledgeDocuments.Select(d => d.Source).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (LoadedPack a in packs)
        {
            foreach (LoadedPack b in packs)
            {
                if (ReferenceEquals(a, b))
                {
                    continue;
                }
                sourcesPerPack[a.Name].Overlaps(sourcesPerPack[b.Name])
                    .Should().BeFalse(
                        "packs '{0}' and '{1}' must not share knowledge document filenames", a.Name, b.Name);
            }
        }

        packs.Should().OnlyContain(p => p.KnowledgeDocuments.Count > 0);
    }

    [Fact]
    public void PackSwitch_StartingTaskCategoriesDifferAcrossPacks()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();

        var categoryIdsPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.StartingTasks.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

        HashSet<string> defaultIds = categoryIdsPerPack["default"];
        foreach ((string name, HashSet<string> ids) in categoryIdsPerPack)
        {
            if (name == "default")
            {
                continue;
            }
            ids.Overlaps(defaultIds).Should().BeFalse(
                "pack '{0}' should not reuse default-pack starting-task category ids", name);
        }

        foreach (LoadedPack pack in packs.Where(p => p.Name != "default"))
        {
            pack.StartingTasks.Count.Should().BeGreaterThanOrEqualTo(3,
                "pack '{0}' should ship at least three starting-task categories", pack.Name);
        }
    }

    [Fact]
    public void PackSwitch_ChangesPackFingerprint_ForcingReseed()
    {
        IReadOnlyList<LoadedPack> packs = LoadAllShipped();

        HashSet<string> fingerprints = [.. packs.Select(PackContentFingerprint.ComputePackFingerprint)];
        fingerprints.Count.Should().Be(packs.Count,
            "every pack must produce a distinct fingerprint so a pack switch forces the seeder to refresh");
    }
}
