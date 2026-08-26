using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Api.Packs;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Packs;

/// <summary>
/// Blocker #6 extends the pack-switch dimensions to prove that the MCP
/// seed manifest itself is scenario-varying: different packs ship
/// different promo vocabularies, different competitor rosters, and
/// different disruption descriptions — and that reality shows up as
/// materially different rows in the seeded SQLite database.
/// </summary>
public sealed class PackSwitchSeedDimensionsTests : IDisposable
{
    private readonly List<string> _dbPaths = [];

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete([.. _dbPaths]);
    }

    [Fact]
    public void ShippedPacks_ShipDistinctSeedManifests()
    {
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        IReadOnlyList<LoadedPack> packs = [.. loader.DiscoverPacks().Select(loader.Load)];
        packs.Count.Should().BeGreaterThanOrEqualTo(3);

        // Every pack has a non-empty seed manifest (blocker #3 forbids
        // README-only placeholders).
        packs.Should().OnlyContain(p => p.Seed.Promos.Types.Count > 0);
        packs.Should().OnlyContain(p => p.Seed.Stores.Types.Count > 0);
        packs.Should().OnlyContain(p => p.Seed.Supply.DisruptionTypes.Count > 0);

        // Promo type NAMES must be disjoint across packs — a pack that
        // reused the default's `bogo/discount/display/digital/bundle`
        // vocabulary would just be reskinning, not a genuine scenario.
        var promoTypesPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.Seed.Promos.Types.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        foreach (LoadedPack a in packs)
        {
            foreach (LoadedPack b in packs)
            {
                if (ReferenceEquals(a, b)) continue;
                promoTypesPerPack[a.Name].Overlaps(promoTypesPerPack[b.Name])
                    .Should().BeFalse(
                        "packs '{0}' and '{1}' must not share promo type names — reskin != new scenario",
                        a.Name, b.Name);
            }
        }

        // Disruption type keys must be disjoint — the storyline of a
        // "chill_chain" outage in a pet-supply pack should not overlap
        // with the default pack's "logistics" narrative.
        var disruptionTypesPerPack = packs.ToDictionary(
            p => p.Name,
            p => p.Seed.Supply.DisruptionTypes.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (LoadedPack a in packs)
        {
            foreach (LoadedPack b in packs)
            {
                if (ReferenceEquals(a, b)) continue;
                disruptionTypesPerPack[a.Name].Overlaps(disruptionTypesPerPack[b.Name])
                    .Should().BeFalse(
                        "packs '{0}' and '{1}' must not share disruption type keys",
                        a.Name, b.Name);
            }
        }
    }

    [Fact]
    public void ShippedPacks_ProduceDifferentSeedRows_InSqlite()
    {
        // End-to-end: seed the MCP SQLite from each pack and prove the
        // Store types, disruption types, and promo names materialize
        // differently. This is the acceptance guarantee for "MCP seed
        // results change together" when Packs:Active flips.
        var loader = PackLoader.ForDirectory(PackTestPaths.PacksRoot);
        IReadOnlyList<LoadedPack> packs = [.. loader.DiscoverPacks().Select(loader.Load)];

        var storeTypesPerPack = new Dictionary<string, HashSet<string>>();
        var promoTypesPerPack = new Dictionary<string, HashSet<string>>();
        var disruptionsPerPack = new Dictionary<string, HashSet<string>>();

        foreach (LoadedPack pack in packs)
        {
            string dbPath = SqliteTestCleanup.NewDbPath($"rp_pack_switch_{pack.Name}");
            _dbPaths.Add(dbPath);

            // Every shipped pack ships its own tenant declaration; wrap
            // it as an in-memory provider for the seeder.
            var tenant = new InlineTenantProvider(pack.Tenant);
            _ = new RetailPulseDb(
                tenant,
                pack.Seed,
                dbPath,
                Path.Combine(pack.RootPath, "pack.yaml"),
                Path.Combine(pack.RootPath, "seed"));

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            storeTypesPerPack[pack.Name] = ReadDistinct(conn,
                "SELECT DISTINCT StoreName FROM StoreMetrics");
            promoTypesPerPack[pack.Name] = ReadDistinct(conn,
                "SELECT DISTINCT PromoType FROM PromoHistory");
            disruptionsPerPack[pack.Name] = ReadDistinct(conn,
                "SELECT DISTINCT DisruptionType FROM SupplyDisruptions");
        }

        // At least three shipped packs must produce three distinct
        // store-type rosters, promo-type rosters, and disruption
        // vocabularies in the seeded DB.
        storeTypesPerPack.Values.Select(s => string.Join(",", s.OrderBy(x => x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count().Should().Be(storeTypesPerPack.Count,
                "each pack must materialize a distinct store-type roster in the seeded DB");

        promoTypesPerPack.Values.Select(s => string.Join(",", s.OrderBy(x => x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count().Should().Be(promoTypesPerPack.Count,
                "each pack must materialize a distinct promo-type roster");

        disruptionsPerPack.Values.Select(s => string.Join(",", s.OrderBy(x => x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count().Should().Be(disruptionsPerPack.Count,
                "each pack must materialize a distinct disruption vocabulary");
    }

    private static HashSet<string> ReadDistinct(SqliteConnection conn, string sql)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }
        return values;
    }

    private sealed class InlineTenantProvider(TenantConfiguration tenant) : ITenantProvider
    {
        public TenantConfiguration GetTenant() => tenant;
    }
}
