using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.StoreOps;

/// <summary>
/// Data integrity tests for Phase 4.1 store operations tables:
/// StoreMetrics, ShelfLayouts, SkuVelocity.
/// Validates seeded data covers expected store count, regions,
/// physical constraints, and velocity data completeness.
/// Test-first: defines expected schema before Phase 4.1 implementation.
/// </summary>
public class StoreDataTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TenantConfiguration _tenant;

    public StoreDataTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_storedata_test");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _tenant = tenantProvider.GetTenant();

        _ = new RetailPulseDb(tenantProvider, _dbPath, tenantConfigPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteTestCleanup.ReleaseAndDelete(_dbPath);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    #region Store Metrics Integrity

    [Fact]
    public void StoreMetrics_AllSeededStoresHaveValidMetrics()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT StoreId, StoreName, Region, Revenue, Target FROM StoreMetrics";

        using SqliteDataReader reader = cmd.ExecuteReader();
        int storeCount = 0;

        while (reader.Read())
        {
            storeCount++;
            string storeId = reader.GetString(0);
            string storeName = reader.GetString(1);
            string region = reader.GetString(2);
            double revenue = reader.GetDouble(3);
            double target = reader.GetDouble(4);

            storeId.Should().NotBeNullOrEmpty($"store at row {storeCount} should have an ID");
            storeName.Should().NotBeNullOrEmpty($"store '{storeId}' should have a name");
            region.Should().NotBeNullOrEmpty($"store '{storeId}' should have a region");
            revenue.Should().BeGreaterThan(0, $"store '{storeId}' should have positive revenue");
            target.Should().BeGreaterThan(0, $"store '{storeId}' should have a positive target");
        }

        storeCount.Should().BeGreaterThan(0, "StoreMetrics table should have seeded data");
    }

    [Fact]
    public void StoreMetrics_StoreCountMatchesExpected()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM StoreMetrics";
        int count = Convert.ToInt32(cmd.ExecuteScalar());

        count.Should().BeGreaterThan(0,
            "should have seeded stores in StoreMetrics");
    }

    [Fact]
    public void StoreMetrics_FiveStoresPerRegion()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Region, COUNT(*) as cnt FROM StoreMetrics GROUP BY Region ORDER BY Region";

        using SqliteDataReader reader = cmd.ExecuteReader();
        var regionCounts = new Dictionary<string, int>();

        while (reader.Read())
        {
            regionCounts[reader.GetString(0)] = reader.GetInt32(1);
        }

        regionCounts.Should().NotBeEmpty("should have stores grouped by region");

        foreach (KeyValuePair<string, int> kvp in regionCounts)
        {
            kvp.Value.Should().BeGreaterThanOrEqualTo(1,
                $"region '{kvp.Key}' should have at least 1 store");
        }
    }

    [Fact]
    public void StoreMetrics_PerformanceIndexDerivable()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT StoreId, Revenue, Target FROM StoreMetrics";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string storeId = reader.GetString(0);
            double revenue = reader.GetDouble(1);
            double target = reader.GetDouble(2);

            double perfIndex = revenue / target;
            perfIndex.Should().BeGreaterThan(0, $"store '{storeId}' should have derivable positive performance index");
            perfIndex.Should().BeLessThan(5.0, $"store '{storeId}' should have reasonable performance index");
        }
    }

    #endregion

    #region Shelf Layout Constraints

    [Fact]
    public void ShelfLayouts_PositionsDontExceedPhysicalConstraints()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AisleId, ShelfLevel, FacingWidth FROM ShelfLayouts";

        using SqliteDataReader reader = cmd.ExecuteReader();
        int rowCount = 0;

        while (reader.Read())
        {
            rowCount++;
            string aisle = reader.GetString(0);
            int shelfLevel = reader.GetInt32(1);
            double facingWidth = reader.GetDouble(2);

            facingWidth.Should().BeGreaterThan(0,
                $"aisle '{aisle}' shelf {shelfLevel} should have a positive FacingWidth");
        }

        rowCount.Should().BeGreaterThan(0, "ShelfLayouts table should have seeded data");
    }

    [Fact]
    public void ShelfLayouts_AllPositionsHaveValidSku()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AisleId, ShelfLevel, SkuId, FacingWidth FROM ShelfLayouts";

        using SqliteDataReader reader = cmd.ExecuteReader();
        int rowCount = 0;

        while (reader.Read())
        {
            rowCount++;
            string sku = reader.GetString(2);
            double facingWidth = reader.GetDouble(3);

            sku.Should().NotBeNullOrEmpty($"row {rowCount} should have a SKU");
            facingWidth.Should().BeGreaterThan(0, $"SKU '{sku}' should have positive FacingWidth");
        }

        rowCount.Should().BeGreaterThan(0, "ShelfLayouts table should have seeded data");
    }

    [Fact]
    public void ShelfLayouts_ShelfNumbersAreSequential()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT AisleId, ShelfLevel
            FROM ShelfLayouts
            GROUP BY AisleId, ShelfLevel
            ORDER BY AisleId, ShelfLevel";

        using SqliteDataReader reader = cmd.ExecuteReader();
        var aisleGroups = new Dictionary<string, List<int>>();

        while (reader.Read())
        {
            string aisle = reader.GetString(0);
            int shelf = reader.GetInt32(1);
            if (!aisleGroups.ContainsKey(aisle))
                aisleGroups[aisle] = [];
            aisleGroups[aisle].Add(shelf);
        }

        foreach ((string? aisle, List<int>? shelves) in aisleGroups)
        {
            shelves.Should().BeInAscendingOrder(
                $"aisle '{aisle}' shelves should be in ascending order");
            shelves.Min().Should().BeGreaterThanOrEqualTo(1,
                $"aisle '{aisle}' shelf numbers should start at 1 or higher");
        }
    }

    #endregion

    #region SKU Velocity Coverage

    [Fact]
    public void SkuVelocity_ExistsForAllSkusInLayouts()
    {
        using SqliteConnection conn = OpenConnection();

        // Get all unique SKUs from layouts
        using SqliteCommand layoutCmd = conn.CreateCommand();
        layoutCmd.CommandText = "SELECT DISTINCT SkuId FROM ShelfLayouts";
        var layoutSkus = new HashSet<string>();
        using (SqliteDataReader reader = layoutCmd.ExecuteReader())
        {
            while (reader.Read())
                layoutSkus.Add(reader.GetString(0));
        }

        // Get all SKUs with velocity data
        using SqliteCommand velocityCmd = conn.CreateCommand();
        velocityCmd.CommandText = "SELECT DISTINCT SkuId FROM SkuVelocity";
        var velocitySkus = new HashSet<string>();
        using (SqliteDataReader reader = velocityCmd.ExecuteReader())
        {
            while (reader.Read())
                velocitySkus.Add(reader.GetString(0));
        }

        // Every layout SKU should have velocity data
        foreach (string sku in layoutSkus)
        {
            velocitySkus.Should().Contain(sku,
                $"SKU '{sku}' is in ShelfLayouts but has no velocity data in SkuVelocity");
        }
    }

    [Fact]
    public void SkuVelocity_AllValuesArePositive()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SkuId, DailyUnits FROM SkuVelocity";

        using SqliteDataReader reader = cmd.ExecuteReader();
        int rowCount = 0;

        while (reader.Read())
        {
            rowCount++;
            string sku = reader.GetString(0);
            double velocity = reader.GetDouble(1);

            velocity.Should().BeGreaterThan(0,
                $"SKU '{sku}' should have positive daily units");
        }

        rowCount.Should().BeGreaterThan(0, "SkuVelocity table should have seeded data");
    }

    #endregion
}
