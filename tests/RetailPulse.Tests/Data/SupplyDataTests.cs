using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Data integrity tests for supply chain tables:
/// InventoryLevels, SupplyDisruptions, FulfillmentRates.
/// Validates seeded data covers all tenant brands, statuses are valid,
/// numeric ranges are realistic, and relationships are consistent.
/// Test-first: defines expected data contracts before implementation exists.
/// </summary>
public class SupplyDataTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TenantConfiguration _tenant;

    private static readonly string[] ExpectedBrands =
    [
        "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
        "FreshMart", "Harvest Table",
        "Apex Grill", "Coastline Tacos",
        "Pinnacle Hardware", "Summit Outdoor",
        "ClearDesk",
        "Urban Living", "Foundry Home"
    ];

    private static readonly string[] ExpectedRegions =
    [
        "Northeast", "Southeast", "Midwest", "Southwest", "West Coast", "Pacific Northwest"
    ];

    private static readonly string[] ValidInventoryStatuses =
    [
        "healthy", "low", "critical", "out_of_stock"
    ];

    private static readonly string[] ValidDisruptionSeverities =
    [
        "low", "medium", "high", "critical"
    ];

    private static readonly string[] ValidDisruptionTypes =
    [
        "logistics", "supplier", "weather", "demand_surge"
    ];

    public SupplyDataTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_supplydata_test_{Guid.NewGuid():N}.db");
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
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    #region InventoryLevels Table

    [Fact]
    public void InventoryLevels_TableExists()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='InventoryLevels'";
        object? result = cmd.ExecuteScalar();
        result.Should().NotBeNull("InventoryLevels table should exist in seeded database");
    }

    [Fact]
    public void InventoryLevels_HasRows()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM InventoryLevels";
        long count = Convert.ToInt64(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0, "InventoryLevels should have seeded data");
    }

    [Fact]
    public void InventoryLevels_CoversAllTenantBrands()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Brand FROM InventoryLevels ORDER BY Brand";

        var brands = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) brands.Add(reader.GetString(0));

        foreach (string expectedBrand in ExpectedBrands)
        {
            brands.Should().Contain(expectedBrand,
                $"InventoryLevels should have data for {expectedBrand}");
        }
    }

    [Fact]
    public void InventoryLevels_StatusesAreValid()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Status FROM InventoryLevels";

        var statuses = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) statuses.Add(reader.GetString(0).ToLower(System.Globalization.CultureInfo.CurrentCulture));

        foreach (string status in statuses)
        {
            status.Should().BeOneOf(ValidInventoryStatuses,
                $"'{status}' is not a valid inventory status");
        }
    }

    [Fact]
    public void InventoryLevels_DaysOfSupplyIsPositive()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM InventoryLevels WHERE DaysOfSupply < 0";
        long negativeCount = Convert.ToInt64(cmd.ExecuteScalar());
        negativeCount.Should().Be(0, "days of supply values must not be negative");
    }

    [Fact]
    public void InventoryLevels_SafetyStockReasonable()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(SafetyStock) FROM InventoryLevels";
        long maxSafetyStock = Convert.ToInt64(cmd.ExecuteScalar());
        maxSafetyStock.Should().BeLessThanOrEqualTo(100000,
            "safety stock should not exceed 100,000 units — unrealistic threshold");
    }

    [Fact]
    public void InventoryLevels_CoversAllRegions()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Region FROM InventoryLevels ORDER BY Region";

        var regions = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) regions.Add(reader.GetString(0));

        regions.Should().HaveCountGreaterThanOrEqualTo(ExpectedRegions.Length,
            "inventory should cover all regions");
    }

    [Fact]
    public void InventoryLevels_CurrentStockNonNegative()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM InventoryLevels WHERE CurrentStock < 0";
        long negativeCount = Convert.ToInt64(cmd.ExecuteScalar());
        negativeCount.Should().Be(0, "current stock cannot be negative");
    }

    #endregion

    #region SupplyDisruptions Table

    [Fact]
    public void SupplyDisruptions_TableExists()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SupplyDisruptions'";
        object? result = cmd.ExecuteScalar();
        result.Should().NotBeNull("SupplyDisruptions table should exist in seeded database");
    }

    [Fact]
    public void SupplyDisruptions_HasRows()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SupplyDisruptions";
        long count = Convert.ToInt64(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0, "SupplyDisruptions should have seeded data");
    }

    [Fact]
    public void SupplyDisruptions_SeveritiesAreValid()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Severity FROM SupplyDisruptions";

        var severities = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) severities.Add(reader.GetString(0).ToLower(System.Globalization.CultureInfo.CurrentCulture));

        foreach (string severity in severities)
        {
            severity.Should().BeOneOf(ValidDisruptionSeverities,
                $"'{severity}' is not a valid disruption severity");
        }
    }

    [Fact]
    public void SupplyDisruptions_TypesAreValid()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT DisruptionType FROM SupplyDisruptions";

        var types = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) types.Add(reader.GetString(0).ToLower(System.Globalization.CultureInfo.CurrentCulture));

        foreach (string type in types)
        {
            type.Should().BeOneOf(ValidDisruptionTypes,
                $"'{type}' is not a valid disruption type");
        }
    }

    [Fact]
    public void SupplyDisruptions_HasStartDate()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SupplyDisruptions WHERE StartDate IS NULL OR StartDate = ''";
        long nullCount = Convert.ToInt64(cmd.ExecuteScalar());
        nullCount.Should().Be(0, "all disruptions must have a start date");
    }

    #endregion

    #region FulfillmentRates Table

    [Fact]
    public void FulfillmentRates_TableExists()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='FulfillmentRates'";
        object? result = cmd.ExecuteScalar();
        result.Should().NotBeNull("FulfillmentRates table should exist in seeded database");
    }

    [Fact]
    public void FulfillmentRates_HasRows()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FulfillmentRates";
        long count = Convert.ToInt64(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0, "FulfillmentRates should have seeded data");
    }

    [Fact]
    public void FulfillmentRates_CoversAllBrands()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Brand FROM FulfillmentRates ORDER BY Brand";

        var brands = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) brands.Add(reader.GetString(0));

        foreach (string expectedBrand in ExpectedBrands)
        {
            brands.Should().Contain(expectedBrand,
                $"FulfillmentRates should have data for {expectedBrand}");
        }
    }

    [Fact]
    public void FulfillmentRates_RatesBetween0And100()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(FillRate), MAX(FillRate) FROM FulfillmentRates";
        using SqliteDataReader reader = cmd.ExecuteReader();
        reader.Read();
        double min = reader.GetDouble(0);
        double max = reader.GetDouble(1);

        min.Should().BeGreaterThanOrEqualTo(0, "fulfillment rate cannot be negative");
        max.Should().BeLessThanOrEqualTo(100, "fulfillment rate cannot exceed 100%");
    }

    [Fact]
    public void FulfillmentRates_MostAbove80Percent()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                CAST(SUM(CASE WHEN FillRate >= 80 THEN 1 ELSE 0 END) AS REAL) /
                CAST(COUNT(*) AS REAL) * 100.0
            FROM FulfillmentRates";
        double aboveThreshold = Convert.ToDouble(cmd.ExecuteScalar());

        aboveThreshold.Should().BeGreaterThanOrEqualTo(60,
            "at least 60% of fulfillment rates should be >=80% for realistic data");
    }

    [Fact]
    public void FulfillmentRates_FillRatesRealistic()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AVG(FillRate) FROM FulfillmentRates";
        double avgRate = Convert.ToDouble(cmd.ExecuteScalar());

        avgRate.Should().BeGreaterThanOrEqualTo(70, "average fill rate should be realistic (>=70%)");
        avgRate.Should().BeLessThanOrEqualTo(99, "average fill rate should be realistic (<=99%)");
    }

    [Fact]
    public void FulfillmentRates_CoversAllRegions()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Region FROM FulfillmentRates ORDER BY Region";

        var regions = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) regions.Add(reader.GetString(0));

        regions.Should().HaveCountGreaterThanOrEqualTo(ExpectedRegions.Length,
            "fulfillment rates should cover all regions");
    }

    #endregion

    #region Cross-Table Consistency

    [Fact]
    public void AllSupplyTables_ShareSameBrandSet()
    {
        using SqliteConnection conn = OpenConnection();

        List<string> inventoryBrands = GetDistinctValues(conn, "InventoryLevels", "Brand");
        List<string> fulfillmentBrands = GetDistinctValues(conn, "FulfillmentRates", "Brand");

        // All fulfillment brands should also have inventory data
        foreach (string brand in fulfillmentBrands)
        {
            inventoryBrands.Should().Contain(brand,
                $"brand '{brand}' in FulfillmentRates should also appear in InventoryLevels");
        }
    }

    private static List<string> GetDistinctValues(SqliteConnection conn, string table, string column)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT {column} FROM {table} ORDER BY {column}";
        var values = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    #endregion
}
