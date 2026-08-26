using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.Tests.TestInfrastructure;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Data integrity tests for the DemandHistory and SeasonalFactors tables.
/// Validates that seeded data covers all tenant.yaml brands/regions,
/// spans the required time period, and contains meaningful seasonal patterns.
/// </summary>
public class DemandDataTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TenantConfiguration _tenant;

    // Known tenant.yaml brands and regions
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

    private static readonly string[] ExpectedChannels =
    [
        "On-Premise", "Off-Premise", "E-Commerce"
    ];

    public DemandDataTests()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");

        _dbPath = SqliteTestCleanup.NewDbPath("retailpulse_data_test");
        var tenantProvider = new FileTenantProvider(tenantConfigPath);
        _tenant = tenantProvider.GetTenant();

        // Create the DB (which triggers seeding)
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
    public void DemandHistory_CoversAllTenantBrands()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Brand FROM DemandHistory ORDER BY Brand";

        var seededBrands = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            seededBrands.Add(reader.GetString(0));

        foreach (string expected in ExpectedBrands)
        {
            seededBrands.Should().Contain(expected,
                $"brand '{expected}' from tenant.yaml should be seeded in DemandHistory");
        }
    }

    [Theory]
    [InlineData("Sierra Gold Tequila")]
    [InlineData("FreshMart")]
    [InlineData("Apex Grill")]
    [InlineData("Pinnacle Hardware")]
    [InlineData("ClearDesk")]
    [InlineData("Urban Living")]
    [InlineData("Foundry Home")]
    public void DemandHistory_EachBrandHasData(string brand)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DemandHistory WHERE Brand = @brand";
        cmd.Parameters.AddWithValue("@brand", brand);

        int count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0,
            $"brand '{brand}' should have demand history records");
    }

    #endregion

    #region Region Coverage

    [Fact]
    public void DemandHistory_CoversAllTenantRegions()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Region FROM DemandHistory ORDER BY Region";

        var seededRegions = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            seededRegions.Add(reader.GetString(0));

        foreach (string expected in ExpectedRegions)
        {
            seededRegions.Should().Contain(expected,
                $"region '{expected}' from tenant.yaml should be seeded in DemandHistory");
        }
    }

    [Theory]
    [InlineData("Northeast")]
    [InlineData("Southeast")]
    [InlineData("Midwest")]
    [InlineData("Southwest")]
    [InlineData("West Coast")]
    [InlineData("Pacific Northwest")]
    public void DemandHistory_EachRegionHasData(string region)
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DemandHistory WHERE Region = @region";
        cmd.Parameters.AddWithValue("@region", region);

        int count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0,
            $"region '{region}' should have demand history records");
    }

    #endregion

    #region Time Span

    [Fact]
    public void DemandHistory_SpansAtLeast12Months()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(Date), MAX(Date) FROM DemandHistory";

        using SqliteDataReader reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        var minDate = DateOnly.Parse(reader.GetString(0));
        var maxDate = DateOnly.Parse(reader.GetString(1));

        int monthSpan = ((maxDate.Year - minDate.Year) * 12) + (maxDate.Month - minDate.Month);
        monthSpan.Should().BeGreaterThanOrEqualTo(11,
            "demand history should span at least 12 months (11+ month difference)");
    }

    [Fact]
    public void DemandHistory_NoContinuousGapsLargerThan2Days()
    {
        // Check for gaps in the data for a single brand/region/channel combo
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Date FROM DemandHistory
            WHERE Brand = 'Sierra Gold Tequila' AND Region = 'Northeast' AND Channel = 'Off-Premise'
            ORDER BY Date
            """;

        var dates = new List<DateOnly>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            dates.Add(DateOnly.Parse(reader.GetString(0)));

        dates.Should().HaveCountGreaterThan(300, "should have ~365 days of data");

        for (int i = 1; i < dates.Count; i++)
        {
            int gap = dates[i].DayNumber - dates[i - 1].DayNumber;
            gap.Should().BeLessThanOrEqualTo(2,
                $"no gaps > 2 days should exist between {dates[i - 1]} and {dates[i]}");
        }
    }

    [Fact]
    public void DemandHistory_HasDailyGranularity()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT Date) FROM DemandHistory
            WHERE Brand = 'Sierra Gold Tequila' AND Region = 'Northeast' AND Channel = 'Off-Premise'
            """;

        int distinctDays = Convert.ToInt32(cmd.ExecuteScalar());
        distinctDays.Should().BeGreaterThanOrEqualTo(360,
            "should have near-daily data for a full year");
    }

    #endregion

    #region Seasonal Patterns

    [Fact]
    public void DemandHistory_SpiritsHolidaySeasonHigherThanAugust()
    {
        // Nov-Dec should have higher volume than August for spirits brands
        using SqliteConnection conn = OpenConnection();

        double novDecVolume = GetAverageMonthlyVolume(conn, "Sierra Gold Tequila", [11, 12]);
        double augVolume = GetAverageMonthlyVolume(conn, "Sierra Gold Tequila", [8]);

        novDecVolume.Should().BeGreaterThan(augVolume,
            "spirits demand in Nov-Dec should exceed August due to holiday seasonality");
    }

    [Fact]
    public void DemandHistory_HomeImprovementSpringHigherThanWinter()
    {
        // Spring (Mar-May) should be higher than winter (Jan-Feb) for home improvement
        using SqliteConnection conn = OpenConnection();

        double springVolume = GetAverageMonthlyVolume(conn, "Pinnacle Hardware", [3, 4, 5]);
        double winterVolume = GetAverageMonthlyVolume(conn, "Pinnacle Hardware", [1, 2]);

        springVolume.Should().BeGreaterThan(winterVolume,
            "home improvement demand in spring should exceed winter");
    }

    [Fact]
    public void DemandHistory_QsrSummerHigherThanWinter()
    {
        // Summer (Jun-Aug) should be higher than winter (Jan-Feb) for QSR
        using SqliteConnection conn = OpenConnection();

        double summerVolume = GetAverageMonthlyVolume(conn, "Apex Grill", [6, 7, 8]);
        double winterVolume = GetAverageMonthlyVolume(conn, "Apex Grill", [1, 2]);

        summerVolume.Should().BeGreaterThan(winterVolume,
            "QSR demand in summer should exceed winter");
    }

    private static double GetAverageMonthlyVolume(SqliteConnection conn, string brand, int[] months)
    {
        string monthFilter = string.Join(",", months);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT AVG(Volume) FROM DemandHistory
            WHERE Brand = @brand
            AND CAST(SUBSTR(Date, 6, 2) AS INTEGER) IN ({monthFilter})
            """;
        cmd.Parameters.AddWithValue("@brand", brand);

        object? result = cmd.ExecuteScalar();
        return result is DBNull ? 0 : Convert.ToDouble(result);
    }

    #endregion

    #region SeasonalFactors Table

    [Fact]
    public void SeasonalFactors_CoversAllTenantCategories()
    {
        var expectedCategories = _tenant.Brands.Select(b => b.Category).Distinct().ToList();

        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Category FROM SeasonalFactors ORDER BY Category";

        var seededCategories = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            seededCategories.Add(reader.GetString(0));

        foreach (string? expected in expectedCategories)
        {
            seededCategories.Should().Contain(expected,
                $"category '{expected}' from tenant.yaml should have seasonal factors");
        }
    }

    [Fact]
    public void SeasonalFactors_MultipliersInReasonableRange()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(Multiplier), MAX(Multiplier) FROM SeasonalFactors";

        using SqliteDataReader reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        double minMultiplier = reader.GetDouble(0);
        double maxMultiplier = reader.GetDouble(1);

        minMultiplier.Should().BeGreaterThanOrEqualTo(0.5, "minimum multiplier should be reasonable");
        maxMultiplier.Should().BeLessThanOrEqualTo(2.0, "maximum multiplier should be reasonable");
    }

    [Fact]
    public void SeasonalFactors_AllMonthsAreValid()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Month FROM SeasonalFactors ORDER BY Month";

        var months = new List<int>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            months.Add(reader.GetInt32(0));

        months.Should().OnlyContain(m => m >= 1 && m <= 12);
    }

    [Fact]
    public void SeasonalFactors_SpiritsDecemberIsHighest()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Month, Multiplier FROM SeasonalFactors
            WHERE Category = 'Spirits'
            ORDER BY Multiplier DESC
            LIMIT 1
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        int peakMonth = reader.GetInt32(0);
        double peakMultiplier = reader.GetDouble(1);

        peakMonth.Should().BeOneOf([11, 12], "spirits peak should be Nov or Dec");
        peakMultiplier.Should().BeGreaterThan(1.0, "peak month should boost demand");
    }

    [Fact]
    public void SeasonalFactors_HaveDescriptions()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM SeasonalFactors
            WHERE Description IS NOT NULL AND Description != ''
            """;

        int count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(0,
            "at least some seasonal factors should have descriptions");
    }

    #endregion

    #region Volume Integrity

    [Fact]
    public void DemandHistory_AllVolumesPositive()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DemandHistory WHERE Volume <= 0";

        int negativeCount = Convert.ToInt32(cmd.ExecuteScalar());
        negativeCount.Should().Be(0, "all demand volumes should be positive");
    }

    [Fact]
    public void DemandHistory_AllUnitsPositive()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DemandHistory WHERE Units <= 0";

        int negativeCount = Convert.ToInt32(cmd.ExecuteScalar());
        negativeCount.Should().Be(0, "all demand units should be positive");
    }

    [Fact]
    public void DemandHistory_AllChannelsCovered()
    {
        using SqliteConnection conn = OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Channel FROM DemandHistory ORDER BY Channel";

        var seededChannels = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            seededChannels.Add(reader.GetString(0));

        foreach (string expected in ExpectedChannels)
        {
            seededChannels.Should().Contain(expected,
                $"channel '{expected}' from tenant.yaml should be seeded in DemandHistory");
        }
    }

    #endregion
}
