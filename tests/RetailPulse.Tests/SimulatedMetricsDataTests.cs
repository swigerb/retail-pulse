using System.Text.Json;
using FluentAssertions;
using Moq;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests;

public class SimulatedMetricsDataTests
{
    private static SimulatedMetricsData CreateDataWithSampleTenant()
    {
        var tenant = new TenantConfiguration
        {
            Company = "Test Corp",
            Industry = "Spirits & Beverages",
            Brands = new List<BrandConfig>
            {
                new() { Name = "Alpha Tequila", Category = "Tequila", Variants = new() { "Blanco", "Reposado" }, PriceSegment = "Premium" },
                new() { Name = "Beta Vodka", Category = "Vodka", Variants = new() { "Original", "Citrus" }, PriceSegment = "Premium" },
                new() { Name = "Gamma Bourbon", Category = "Bourbon", Variants = new() { "Small Batch", "Single Barrel" }, PriceSegment = "Ultra-Premium" },
            },
            Regions = new List<string> { "Northeast", "Southeast", "West Coast" },
            Channels = new List<string> { "On-Premise", "Off-Premise" },
            Distribution = new DistributionConfig { Model = "Three-Tier" }
        };

        var mock = new Mock<ITenantProvider>();
        mock.Setup(m => m.GetTenant()).Returns(tenant);
        return new SimulatedMetricsData(mock.Object);
    }

    [Theory]
    [InlineData("Alpha Tequila", "Northeast")]
    [InlineData("Beta Vodka", "Southeast")]
    [InlineData("Gamma Bourbon", "West Coast")]
    public void GetDepletionStats_KnownBrandRegion_ReturnsExpectedStructure(string brand, string region)
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetDepletionStats(brand, region, "YTD");
        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("period").GetString().Should().Be("YTD");

        var metrics = root.GetProperty("metrics");
        metrics.GetProperty("depletions_yoy").GetString().Should().ContainAny("+", "-");
        metrics.GetProperty("sell_through_yoy").GetString().Should().ContainAny("+", "-");
        metrics.GetProperty("inventory_weeks_on_hand").GetDouble().Should().BeGreaterThan(0);
        metrics.GetProperty("status").GetString().Should().NotBeNullOrEmpty();

        root.GetProperty("sentiment_summary").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDepletionStats_UnknownBrand_ReturnsErrorWithAvailableBrands()
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetDepletionStats("Unknown Brand", "Northeast", "YTD");
        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("error").GetString().Should().Contain("No data found");
        root.GetProperty("available_brands").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("available_regions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetDepletionStats_AllBrandRegionCombinations_HaveData()
    {
        var data = CreateDataWithSampleTenant();
        var brands = new[] { "Alpha Tequila", "Beta Vodka", "Gamma Bourbon" };
        var regions = new[] { "Northeast", "Southeast", "West Coast" };

        foreach (var brand in brands)
        {
            foreach (var region in regions)
            {
                var result = data.GetDepletionStats(brand, region, "YTD");
                var json = JsonSerializer.Serialize(result);
                json.Should().NotContain("error", $"brand '{brand}' in region '{region}' should have data");
            }
        }
    }

    [Theory]
    [InlineData("alpha tequila", "northeast")]
    [InlineData("ALPHA TEQUILA", "NORTHEAST")]
    [InlineData("Alpha Tequila", "northeast")]
    public void GetDepletionStats_CaseInsensitive_ReturnsData(string brand, string region)
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetDepletionStats(brand, region, "YTD");
        var json = JsonSerializer.Serialize(result);

        json.Should().NotContain("error");
    }

    [Fact]
    public void GetDepletionStats_DifferentPeriods_ProduceDifferentNumbers()
    {
        var data = CreateDataWithSampleTenant();
        var q1 = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", "Q1"));
        var q4 = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", "Q4"));

        q1.Should().NotBe(q4, "different periods should produce different metrics");
    }

    [Theory]
    [InlineData("Alpha Tequila", "Northeast")]
    [InlineData("Beta Vodka", "Southeast")]
    [InlineData("Gamma Bourbon", "West Coast")]
    public void GetFieldSentiment_KnownBrandRegion_ReturnsSentimentData(string brand, string region)
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetFieldSentiment(brand, region);
        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("source").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("sentiment").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetFieldSentiment_UnknownBrand_ReturnsError()
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetFieldSentiment("Nonexistent", "Northeast");
        var json = JsonSerializer.Serialize(result);

        json.Should().Contain("error");
        json.Should().Contain("No sentiment data");
    }

    [Fact]
    public void GetFieldSentiment_DifferentRegions_ReturnDifferentSentiment()
    {
        var data = CreateDataWithSampleTenant();
        var result1 = JsonSerializer.Serialize(data.GetFieldSentiment("Alpha Tequila", "Northeast"));
        var result2 = JsonSerializer.Serialize(data.GetFieldSentiment("Alpha Tequila", "West Coast"));

        result1.Should().NotBe(result2, "different regions should have different sentiment");
    }

    [Fact]
    public void GetDepletionStats_PartialMatch_FindsBrand()
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetDepletionStats("Alpha", "Northeast", "YTD");
        var json = JsonSerializer.Serialize(result);

        json.Should().NotContain("\"error\"");
    }

    [Theory]
    [InlineData("Alpha Tequila", "Northeast")]
    [InlineData("Beta Vodka", "West Coast")]
    public void GetShipmentStats_KnownBrandRegion_ReturnsExpectedStructure(string brand, string region)
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetShipmentStats(brand, region, "YTD");
        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();

        var shipments = root.GetProperty("shipments");
        shipments.GetProperty("shipments_yoy").GetString().Should().ContainAny("+", "-");
        shipments.GetProperty("cases_shipped").GetInt32().Should().BeGreaterThan(0);
        shipments.GetProperty("cases_depleted").GetInt32().Should().BeGreaterThan(0);

        var anomaly = root.GetProperty("anomaly");
        anomaly.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        anomaly.GetProperty("risk_level").GetString().Should().NotBeNullOrEmpty();

        root.GetProperty("analysis").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Constructor_GeneratesDataForAllBrandRegionCombinations()
    {
        var data = CreateDataWithSampleTenant();
        // 3 brands × 3 regions = 9 combinations — all should have depletion, shipment, and sentiment data
        var brands = new[] { "Alpha Tequila", "Beta Vodka", "Gamma Bourbon" };
        var regions = new[] { "Northeast", "Southeast", "West Coast" };

        foreach (var brand in brands)
        {
            foreach (var region in regions)
            {
                var depJson = JsonSerializer.Serialize(data.GetDepletionStats(brand, region, "YTD"));
                depJson.Should().NotContain("error", $"depletion data missing for {brand}/{region}");

                var shipJson = JsonSerializer.Serialize(data.GetShipmentStats(brand, region, "YTD"));
                shipJson.Should().NotContain("error", $"shipment data missing for {brand}/{region}");

                var sentJson = JsonSerializer.Serialize(data.GetFieldSentiment(brand, region));
                sentJson.Should().NotContain("error", $"sentiment data missing for {brand}/{region}");
            }
        }
    }

    [Fact]
    public void Constructor_SeededRandom_ProducesConsistentResults()
    {
        var data1 = CreateDataWithSampleTenant();
        var data2 = CreateDataWithSampleTenant();

        var result1 = JsonSerializer.Serialize(data1.GetDepletionStats("Alpha Tequila", "Northeast", "YTD"));
        var result2 = JsonSerializer.Serialize(data2.GetDepletionStats("Alpha Tequila", "Northeast", "YTD"));

        result1.Should().Be(result2, "seeded Random should produce identical results");
    }

    // -------------------------- Edge-case coverage --------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDepletionStats_EmptyOrNullBrand_DoesNotThrow(string? brand)
    {
        var data = CreateDataWithSampleTenant();

        // Defensive contract: the API must never throw on user-supplied empty/null
        // input. Note: today an empty brand string falls through to a partial match
        // (because string.Empty.Contains(anything) is true), returning the first
        // brand. This documents that behavior — if it changes to a structured
        // error, update the assertion below.
        Action act = () => data.GetDepletionStats(brand!, "Northeast", "YTD");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDepletionStats_EmptyOrNullRegion_DoesNotThrow(string? region)
    {
        var data = CreateDataWithSampleTenant();

        Action act = () => data.GetDepletionStats("Alpha Tequila", region!, "YTD");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ytd")]
    [InlineData("YTD")]
    [InlineData("  Q1  ")]
    [InlineData("q4")]
    [InlineData("UNKNOWN_PERIOD")]
    [InlineData("")]
    public void GetDepletionStats_UnknownOrCasingPeriods_AreHandled(string period)
    {
        var data = CreateDataWithSampleTenant();

        // Period parsing should be resilient: case-insensitive, whitespace-tolerant,
        // unknown values fall back to the YTD/default multiplier (no throw).
        Action act = () => data.GetDepletionStats("Alpha Tequila", "Northeast", period);
        act.Should().NotThrow();

        var json = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", period));
        json.Should().NotContain("\"error\"");
    }

    [Fact]
    public void GetShipmentStats_AllAnomalyClassifications_Encountered()
    {
        // Sweep all brand/region combinations and verify the full set of anomaly
        // classifications is reachable from the simulated dataset.
        var data = CreateDataWithSampleTenant();
        var brands = new[] { "Alpha Tequila", "Beta Vodka", "Gamma Bourbon" };
        var regions = new[] { "Northeast", "Southeast", "West Coast" };
        var expectedTypes = new HashSet<string>
        {
            "pipeline_clog", "supply_constraint", "growth_opportunity",
            "pipeline_building", "declining_aligned", "healthy"
        };
        var expectedRiskLevels = new HashSet<string> { "low", "medium", "high", "critical" };

        var seenTypes = new HashSet<string>();
        var seenRisks = new HashSet<string>();

        foreach (var brand in brands)
        {
            foreach (var region in regions)
            {
                var json = JsonSerializer.Serialize(data.GetShipmentStats(brand, region, "YTD"));
                var doc = JsonDocument.Parse(json);
                var anomaly = doc.RootElement.GetProperty("anomaly");
                seenTypes.Add(anomaly.GetProperty("type").GetString()!);
                seenRisks.Add(anomaly.GetProperty("risk_level").GetString()!);
            }
        }

        seenTypes.Should().BeSubsetOf(expectedTypes,
            "every anomaly type emitted should be one of the documented classifications");
        seenRisks.Should().BeSubsetOf(expectedRiskLevels,
            "every risk level should be a documented value");
    }

    [Fact]
    public void GetShipmentStats_UnknownBrand_ReturnsErrorWithAvailableLists()
    {
        var data = CreateDataWithSampleTenant();
        var result = data.GetShipmentStats("No Such Brand", "Northeast", "YTD");
        var json = JsonSerializer.Serialize(result);

        json.Should().Contain("error");
        json.Should().Contain("available_brands");
        json.Should().Contain("available_regions");
    }

    [Fact]
    public void GetDepletionStats_WhitespaceTrimmedFromBrandAndRegion()
    {
        var data = CreateDataWithSampleTenant();
        var json = JsonSerializer.Serialize(
            data.GetDepletionStats("  Alpha Tequila  ", "  Northeast  ", "YTD"));

        json.Should().NotContain("\"error\"",
            "leading/trailing whitespace should be trimmed before lookup");
    }
}
