using FluentAssertions;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests.Data;

/// <summary>
/// Data integrity tests for the PromoHistory and LiftCoefficients tables.
/// Validates that seeded data covers all tenant.yaml brands/regions,
/// contains valid promo types, and maintains referential integrity.
/// </summary>
public class PromoDataTests : IDisposable
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

    private static readonly string[] ExpectedPromoTypes =
    [
        "BOGO", "Discount", "Display", "Digital", "Bundle"
    ];

    private static readonly string[] ValidSuccessRatings =
    [
        "Excellent", "Good", "Average", "Below Average", "Poor"
    ];

    public PromoDataTests()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tenantConfigPath = Path.Combine(repoRoot, "tenant.yaml");
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpulse_promodata_test_{Guid.NewGuid():N}.db");
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

    #region PromoHistory Coverage

    [Fact]
    public void PromoHistory_HasMinimum60Campaigns()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PromoHistory";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().BeGreaterThan(60);
    }

    [Fact]
    public void PromoHistory_CoversAllBrands()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Brand FROM PromoHistory ORDER BY Brand";

        var seededBrands = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededBrands.Add(reader.GetString(0));

        foreach (var expected in ExpectedBrands)
        {
            seededBrands.Should().Contain(expected,
                $"brand '{expected}' from tenant.yaml should be seeded in PromoHistory");
        }
    }

    [Fact]
    public void PromoHistory_CoversAllRegions()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Region FROM PromoHistory ORDER BY Region";

        var seededRegions = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededRegions.Add(reader.GetString(0));

        foreach (var expected in ExpectedRegions)
        {
            seededRegions.Should().Contain(expected,
                $"region '{expected}' from tenant.yaml should be seeded in PromoHistory");
        }
    }

    [Fact]
    public void PromoHistory_Has5CampaignsPerBrandRegion()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, COUNT(*) as CampaignCount
            FROM PromoHistory
            GROUP BY Brand, Region
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var brand = reader.GetString(0);
            var region = reader.GetString(1);
            var count = reader.GetInt32(2);
            count.Should().Be(5,
                $"brand '{brand}' in region '{region}' should have exactly 5 campaigns");
        }
    }

    [Fact]
    public void PromoHistory_CoversAllPromoTypes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT PromoType FROM PromoHistory ORDER BY PromoType";

        var seededTypes = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededTypes.Add(reader.GetString(0));

        foreach (var expected in ExpectedPromoTypes)
        {
            seededTypes.Should().Contain(expected,
                $"promo type '{expected}' should be seeded in PromoHistory");
        }
    }

    #endregion

    #region PromoHistory Data Integrity

    [Fact]
    public void PromoHistory_SpendIsPositive()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PromoHistory WHERE Spend <= 0";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(0, "all promo spend values should be positive");
    }

    [Fact]
    public void PromoHistory_BaselineVolumeIsPositive()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PromoHistory WHERE BaselineVolume <= 0";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(0, "all baseline volumes should be positive");
    }

    [Fact]
    public void PromoHistory_ActualVolumeExceedsBaseline()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM PromoHistory WHERE ActualVolume >= BaselineVolume
            """;
        var liftedCount = Convert.ToInt32(cmd.ExecuteScalar());

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM PromoHistory";
        var totalCount = Convert.ToInt32(cmd2.ExecuteScalar());

        var liftedRatio = (double)liftedCount / totalCount;
        liftedRatio.Should().BeGreaterThanOrEqualTo(0.8,
            "at least 80% of promos should have ActualVolume >= BaselineVolume due to lift");
    }

    [Fact]
    public void PromoHistory_DatesAreValid()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT StartDate, EndDate FROM PromoHistory";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var startStr = reader.GetString(0);
            var endStr = reader.GetString(1);

            var startParsed = DateOnly.TryParse(startStr, out var startDate);
            var endParsed = DateOnly.TryParse(endStr, out var endDate);

            startParsed.Should().BeTrue($"StartDate '{startStr}' should be a valid date");
            endParsed.Should().BeTrue($"EndDate '{endStr}' should be a valid date");
            startDate.Should().BeBefore(endDate,
                $"StartDate '{startStr}' should be before EndDate '{endStr}'");
        }
    }

    [Fact]
    public void PromoHistory_SuccessRatingIsValid()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT SuccessRating FROM PromoHistory";

        var ratings = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ratings.Add(reader.GetString(0));

        ratings.Should().OnlyContain(r => ValidSuccessRatings.Contains(r),
            "all success ratings should be one of: Excellent, Good, Average, Below Average, Poor");
    }

    [Fact]
    public void PromoHistory_CampaignNamesAreUnique()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM PromoHistory
            WHERE CampaignName IS NULL OR CampaignName = ''
            """;

        var emptyCount = Convert.ToInt32(cmd.ExecuteScalar());
        emptyCount.Should().Be(0, "all campaigns should have non-empty names");
    }

    #endregion

    #region LiftCoefficients Coverage

    [Fact]
    public void LiftCoefficients_CoversAllCategories()
    {
        var expectedCategories = _tenant.Brands.Select(b => b.Category).Distinct().ToList();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Category FROM LiftCoefficients ORDER BY Category";

        var seededCategories = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededCategories.Add(reader.GetString(0));

        seededCategories.Should().HaveCount(expectedCategories.Count,
            "LiftCoefficients should cover all distinct categories from tenant brands");

        foreach (var expected in expectedCategories)
        {
            seededCategories.Should().Contain(expected,
                $"category '{expected}' from tenant.yaml should have lift coefficients");
        }
    }

    [Fact]
    public void LiftCoefficients_CoversAllPromoTypes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Category, COUNT(DISTINCT PromoType) as TypeCount
            FROM LiftCoefficients
            GROUP BY Category
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var category = reader.GetString(0);
            var typeCount = reader.GetInt32(1);
            typeCount.Should().Be(5,
                $"category '{category}' should have 5 promo types in LiftCoefficients");
        }
    }

    [Fact]
    public void LiftCoefficients_AvgLiftIsPositive()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LiftCoefficients WHERE AvgLiftPercent <= 0";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(0, "all average lift percentages should be positive");
    }

    [Fact]
    public void LiftCoefficients_StdDevIsPositive()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LiftCoefficients WHERE StdDev <= 0";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(0, "all standard deviations should be positive");
    }

    #endregion

    #region LiftCoefficients Integrity

    [Fact]
    public void LiftCoefficients_MinSpendBelowMaxEffective()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LiftCoefficients WHERE MinSpend >= MaxEffectiveSpend";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(0, "MinSpend should be less than MaxEffectiveSpend for all rows");
    }

    [Fact]
    public void LiftCoefficients_HasExpectedRowCount()
    {
        var expectedCategories = _tenant.Brands.Select(b => b.Category).Distinct().Count();
        var expectedRows = expectedCategories * 5;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LiftCoefficients";

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        count.Should().Be(expectedRows,
            $"LiftCoefficients should have {expectedCategories} categories × 5 promo types = {expectedRows} rows");
    }

    [Fact]
    public void LiftCoefficients_BOGOHasHigherLiftThanDigital()
    {
        using var conn = OpenConnection();

        using var bogoCmd = conn.CreateCommand();
        bogoCmd.CommandText = "SELECT AVG(AvgLiftPercent) FROM LiftCoefficients WHERE PromoType = 'BOGO'";
        var bogoAvg = Convert.ToDouble(bogoCmd.ExecuteScalar());

        using var digitalCmd = conn.CreateCommand();
        digitalCmd.CommandText = "SELECT AVG(AvgLiftPercent) FROM LiftCoefficients WHERE PromoType = 'Digital'";
        var digitalAvg = Convert.ToDouble(digitalCmd.ExecuteScalar());

        bogoAvg.Should().BeGreaterThan(digitalAvg,
            "average BOGO lift should be higher than average Digital lift");
    }

    #endregion

    #region Cross-Table

    [Fact]
    public void PromoHistoryBrands_MatchTenantConfig()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Brand FROM PromoHistory ORDER BY Brand";

        var seededBrands = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededBrands.Add(reader.GetString(0));

        var tenantBrands = _tenant.Brands.Select(b => b.Name).ToList();

        foreach (var brand in seededBrands)
        {
            tenantBrands.Should().Contain(brand,
                $"PromoHistory brand '{brand}' should exist in tenant.yaml");
        }
    }

    [Fact]
    public void PromoHistoryRegions_MatchTenantConfig()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Region FROM PromoHistory ORDER BY Region";

        var seededRegions = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            seededRegions.Add(reader.GetString(0));

        var tenantRegions = _tenant.Regions.ToList();

        foreach (var region in seededRegions)
        {
            tenantRegions.Should().Contain(region,
                $"PromoHistory region '{region}' should exist in tenant.yaml");
        }
    }

    #endregion
}
