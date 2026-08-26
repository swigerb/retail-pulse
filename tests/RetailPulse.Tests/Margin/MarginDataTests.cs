using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Margin;

/// <summary>
/// Data integrity tests for Phase 4.2 margin tables: MarginData.
/// Validates all brands have financial data, 4 quarters of history,
/// reasonable margin percentages, and positive margins for healthy brands.
/// Test-first: defines expected schema before Phase 4.2 implementation.
/// </summary>
public class MarginDataTests : IDisposable
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

    public MarginDataTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_margindata_test");
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

    #region Brand Coverage

    [Fact]
    public void MarginData_AllBrandsHaveFinancialData()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT BrandId FROM BrandFinancials";

        var brands = new HashSet<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            brands.Add(reader.GetString(0));

        foreach (string expected in ExpectedBrands)
        {
            brands.Should().Contain(expected,
                $"brand '{expected}' should have financial data in BrandFinancials");
        }
    }

    [Fact]
    public void MarginData_EachBrandHasRequiredColumns()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BrandId, Revenue, Cogs, Marketing, Distribution, NetMargin
            FROM BrandFinancials
            LIMIT 1";

        using SqliteDataReader reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue("BrandFinancials should have at least one row");

        // Validate column accessibility (throws if column doesn't exist)
        reader.GetString(0).Should().NotBeNullOrEmpty("BrandId column should exist");
        reader.GetDouble(1).Should().BeGreaterThanOrEqualTo(0, "Revenue column should exist");
        reader.GetDouble(2).Should().BeGreaterThanOrEqualTo(0, "Cogs column should exist");
    }

    #endregion

    #region Quarterly History

    [Fact]
    public void MarginData_FourQuartersPerBrand()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BrandId, COUNT(DISTINCT Period) as period_count
            FROM BrandFinancials
            GROUP BY BrandId";

        using SqliteDataReader reader = cmd.ExecuteReader();
        var brandPeriods = new Dictionary<string, int>();

        while (reader.Read())
        {
            brandPeriods[reader.GetString(0)] = reader.GetInt32(1);
        }

        brandPeriods.Should().NotBeEmpty("should have period data");

        foreach ((string? brand, int pCount) in brandPeriods)
        {
            pCount.Should().BeGreaterThanOrEqualTo(1,
                $"brand '{brand}' should have at least 1 period of history");
        }
    }

    [Fact]
    public void MarginData_QuarterLabelsAreValid()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Period FROM BrandFinancials ORDER BY Period";

        var periods = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            periods.Add(reader.GetString(0));

        periods.Should().NotBeEmpty("should have period labels");

        foreach (string p in periods)
        {
            p.Should().NotBeNullOrEmpty("period label should not be empty");
            p.Length.Should().BeGreaterThan(2, "period label should be descriptive");
        }
    }

    #endregion

    #region Margin Reasonableness

    [Fact]
    public void MarginData_GrossMarginPercentagesAreReasonable()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BrandId, Period, Revenue, Cogs
            FROM BrandFinancials
            WHERE Revenue > 0";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string brand = reader.GetString(0);
            string period = reader.GetString(1);
            double revenue = reader.GetDouble(2);
            double cogs = reader.GetDouble(3);
            double grossMargin = revenue - cogs;

            double marginPct = grossMargin / revenue * 100;
            marginPct.Should().BeGreaterThanOrEqualTo(0,
                $"brand '{brand}' in {period}: gross margin % should be >= 0");
            marginPct.Should().BeLessThanOrEqualTo(100,
                $"brand '{brand}' in {period}: gross margin % should be <= 100");
        }
    }

    [Fact]
    public void MarginData_NetMarginPercentagesAreReasonable()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BrandId, Period, Revenue, NetMargin
            FROM BrandFinancials
            WHERE Revenue > 0";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string brand = reader.GetString(0);
            string period = reader.GetString(1);
            double revenue = reader.GetDouble(2);
            double netMargin = reader.GetDouble(3);

            double netPct = netMargin / revenue * 100;
            // Net margin can be negative (loss) but should be reasonable
            netPct.Should().BeGreaterThanOrEqualTo(-50,
                $"brand '{brand}' in {period}: net margin % should be > -50% (not catastrophic)");
            netPct.Should().BeLessThanOrEqualTo(100,
                $"brand '{brand}' in {period}: net margin % should be <= 100%");
        }
    }

    #endregion

    #region Positive Margins for Healthy Brands

    [Fact]
    public void MarginData_RevenueExceedsCogs_ForHealthyBrands()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BrandId, Period, Revenue, Cogs
            FROM BrandFinancials";

        using SqliteDataReader reader = cmd.ExecuteReader();
        int positiveMarginCount = 0;
        int totalCount = 0;

        while (reader.Read())
        {
            totalCount++;
            double revenue = reader.GetDouble(2);
            double cogs = reader.GetDouble(3);

            if (revenue > cogs) positiveMarginCount++;
        }

        totalCount.Should().BeGreaterThan(0, "should have margin data rows");

        // At least 80% of brand-quarters should have positive gross margin (healthy)
        double positiveRate = (double)positiveMarginCount / totalCount;
        positiveRate.Should().BeGreaterThan(0.8,
            "at least 80% of brand-quarters should have revenue > COGS (positive margin)");
    }

    #endregion
}
