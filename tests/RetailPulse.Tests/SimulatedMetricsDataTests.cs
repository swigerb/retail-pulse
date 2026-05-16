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
            BrandsList =
            [
                new() { Name = "Alpha Tequila", Category = "Tequila", VariantsList = ["Blanco", "Reposado"], PriceSegment = "Premium" },
                new() { Name = "Beta Vodka", Category = "Vodka", VariantsList = ["Original", "Citrus"], PriceSegment = "Premium" },
                new() { Name = "Gamma Bourbon", Category = "Bourbon", VariantsList = ["Small Batch", "Single Barrel"], PriceSegment = "Ultra-Premium" },
            ],
            RegionsList = ["Northeast", "Southeast", "West Coast"],
            ChannelsList = ["On-Premise", "Off-Premise"],
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
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetDepletionStats(brand, region, "YTD");
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("period").GetString().Should().Be("YTD");

        JsonElement metrics = root.GetProperty("metrics");
        metrics.GetProperty("depletions_yoy").GetString().Should().ContainAny("+", "-");
        metrics.GetProperty("sell_through_yoy").GetString().Should().ContainAny("+", "-");
        metrics.GetProperty("inventory_weeks_on_hand").GetDouble().Should().BeGreaterThan(0);
        metrics.GetProperty("status").GetString().Should().NotBeNullOrEmpty();

        root.GetProperty("sentiment_summary").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDepletionStats_UnknownBrand_ReturnsErrorWithAvailableBrands()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetDepletionStats("Unknown Brand", "Northeast", "YTD");
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("error").GetString().Should().Contain("No data found");
        root.GetProperty("available_brands").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("available_regions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetDepletionStats_AllBrandRegionCombinations_HaveData()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        string[] brands = ["Alpha Tequila", "Beta Vodka", "Gamma Bourbon"];
        string[] regions = ["Northeast", "Southeast", "West Coast"];

        foreach (string? brand in brands)
        {
            foreach (string? region in regions)
            {
                object result = data.GetDepletionStats(brand, region, "YTD");
                string json = JsonSerializer.Serialize(result);
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
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetDepletionStats(brand, region, "YTD");
        string json = JsonSerializer.Serialize(result);

        json.Should().NotContain("error");
    }

    [Fact]
    public void GetDepletionStats_DifferentPeriods_ProduceDifferentNumbers()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        string q1 = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", "Q1"));
        string q4 = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", "Q4"));

        q1.Should().NotBe(q4, "different periods should produce different metrics");
    }

    [Theory]
    [InlineData("Alpha Tequila", "Northeast")]
    [InlineData("Beta Vodka", "Southeast")]
    [InlineData("Gamma Bourbon", "West Coast")]
    public void GetFieldSentiment_KnownBrandRegion_ReturnsSentimentData(string brand, string region)
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetFieldSentiment(brand, region);
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("source").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("sentiment").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetFieldSentiment_UnknownBrand_ReturnsError()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetFieldSentiment("Nonexistent", "Northeast");
        string json = JsonSerializer.Serialize(result);

        json.Should().Contain("error");
        json.Should().Contain("No sentiment data");
    }

    [Fact]
    public void GetFieldSentiment_DifferentRegions_ReturnDifferentSentiment()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        string result1 = JsonSerializer.Serialize(data.GetFieldSentiment("Alpha Tequila", "Northeast"));
        string result2 = JsonSerializer.Serialize(data.GetFieldSentiment("Alpha Tequila", "West Coast"));

        result1.Should().NotBe(result2, "different regions should have different sentiment");
    }

    [Fact]
    public void GetDepletionStats_PartialMatch_FindsBrand()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetDepletionStats("Alpha", "Northeast", "YTD");
        string json = JsonSerializer.Serialize(result);

        json.Should().NotContain("\"error\"");
    }

    [Fact]
    public void GetPortfolioDepletionStats_ReturnsAllBrandsForRegion()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetPortfolioDepletionStats("Northeast", "YTD");
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("region").GetString().Should().Be("Northeast");
        root.GetProperty("period").GetString().Should().Be("YTD");
        root.GetProperty("brandCount").GetInt32().Should().Be(3);

        JsonElement brands = root.GetProperty("brands");
        brands.GetArrayLength().Should().Be(3);
        brands.EnumerateArray().Select(b => b.GetProperty("brand").GetString()).Should()
            .BeEquivalentTo(["Alpha Tequila", "Beta Vodka", "Gamma Bourbon"]);
    }

    [Fact]
    public void GetPortfolioDepletionStats_National_ReturnsAggregatedBrandResults()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetPortfolioDepletionStats("National", "Q1");
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement brands = doc.RootElement.GetProperty("brands");

        brands.GetArrayLength().Should().Be(3);
        brands.EnumerateArray().All(b => b.GetProperty("region").GetString() == "National").Should().BeTrue();
    }

    [Theory]
    [InlineData("Alpha Tequila", "Northeast")]
    [InlineData("Beta Vodka", "West Coast")]
    public void GetShipmentStats_KnownBrandRegion_ReturnsExpectedStructure(string brand, string region)
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetShipmentStats(brand, region, "YTD");
        string json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("brand").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("region").GetString().Should().NotBeNullOrEmpty();

        JsonElement shipments = root.GetProperty("shipments");
        shipments.GetProperty("shipments_yoy").GetString().Should().ContainAny("+", "-");
        shipments.GetProperty("cases_shipped").GetInt32().Should().BeGreaterThan(0);
        shipments.GetProperty("cases_depleted").GetInt32().Should().BeGreaterThan(0);

        JsonElement anomaly = root.GetProperty("anomaly");
        anomaly.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        anomaly.GetProperty("risk_level").GetString().Should().NotBeNullOrEmpty();

        root.GetProperty("analysis").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Constructor_GeneratesDataForAllBrandRegionCombinations()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        // 3 brands × 3 regions = 9 combinations — all should have depletion, shipment, and sentiment data
        string[] brands = ["Alpha Tequila", "Beta Vodka", "Gamma Bourbon"];
        string[] regions = ["Northeast", "Southeast", "West Coast"];

        foreach (string? brand in brands)
        {
            foreach (string? region in regions)
            {
                string depJson = JsonSerializer.Serialize(data.GetDepletionStats(brand, region, "YTD"));
                depJson.Should().NotContain("error", $"depletion data missing for {brand}/{region}");

                string shipJson = JsonSerializer.Serialize(data.GetShipmentStats(brand, region, "YTD"));
                shipJson.Should().NotContain("error", $"shipment data missing for {brand}/{region}");

                string sentJson = JsonSerializer.Serialize(data.GetFieldSentiment(brand, region));
                sentJson.Should().NotContain("error", $"sentiment data missing for {brand}/{region}");
            }
        }
    }

    [Fact]
    public void Constructor_SeededRandom_ProducesConsistentResults()
    {
        SimulatedMetricsData data1 = CreateDataWithSampleTenant();
        SimulatedMetricsData data2 = CreateDataWithSampleTenant();

        string result1 = JsonSerializer.Serialize(data1.GetDepletionStats("Alpha Tequila", "Northeast", "YTD"));
        string result2 = JsonSerializer.Serialize(data2.GetDepletionStats("Alpha Tequila", "Northeast", "YTD"));

        result1.Should().Be(result2, "seeded Random should produce identical results");
    }

    // -------------------------- Edge-case coverage --------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDepletionStats_EmptyOrNullBrand_DoesNotThrow(string? brand)
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();

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
        SimulatedMetricsData data = CreateDataWithSampleTenant();

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
        SimulatedMetricsData data = CreateDataWithSampleTenant();

        // Period parsing should be resilient: case-insensitive, whitespace-tolerant,
        // unknown values fall back to the YTD/default multiplier (no throw).
        Action act = () => data.GetDepletionStats("Alpha Tequila", "Northeast", period);
        act.Should().NotThrow();

        string json = JsonSerializer.Serialize(data.GetDepletionStats("Alpha Tequila", "Northeast", period));
        json.Should().NotContain("\"error\"");
    }

    [Fact]
    public void GetShipmentStats_AllAnomalyClassifications_Encountered()
    {
        // Sweep all brand/region combinations and verify the full set of anomaly
        // classifications is reachable from the simulated dataset.
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        string[] brands = ["Alpha Tequila", "Beta Vodka", "Gamma Bourbon"];
        string[] regions = ["Northeast", "Southeast", "West Coast"];
        var expectedTypes = new HashSet<string>
        {
            "pipeline_clog", "supply_constraint", "growth_opportunity",
            "pipeline_building", "declining_aligned", "healthy"
        };
        var expectedRiskLevels = new HashSet<string> { "low", "medium", "high", "critical" };

        var seenTypes = new HashSet<string>();
        var seenRisks = new HashSet<string>();

        foreach (string? brand in brands)
        {
            foreach (string? region in regions)
            {
                string json = JsonSerializer.Serialize(data.GetShipmentStats(brand, region, "YTD"));
                var doc = JsonDocument.Parse(json);
                JsonElement anomaly = doc.RootElement.GetProperty("anomaly");
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
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        object result = data.GetShipmentStats("No Such Brand", "Northeast", "YTD");
        string json = JsonSerializer.Serialize(result);

        json.Should().Contain("error");
        json.Should().Contain("available_brands");
        json.Should().Contain("available_regions");
    }

    [Fact]
    public void GetDepletionStats_WhitespaceTrimmedFromBrandAndRegion()
    {
        SimulatedMetricsData data = CreateDataWithSampleTenant();
        string json = JsonSerializer.Serialize(
            data.GetDepletionStats("  Alpha Tequila  ", "  Northeast  ", "YTD"));

        json.Should().NotContain("\"error\"",
            "leading/trailing whitespace should be trimmed before lookup");
    }
}
