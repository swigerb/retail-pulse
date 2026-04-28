using System.Text.Json;
using FluentAssertions;
using RetailPulse.McpServer.Data;

namespace RetailPulse.Tests;

public class BacardiSimulatedDataTests
{
    [Theory]
    [InlineData("Patrón Silver", "Florida")]
    [InlineData("Angel's Envy", "New York")]
    [InlineData("Grey Goose", "California")]
    [InlineData("Bacardi Superior", "National")]
    [InlineData("Cazadores Blanco", "Texas")]
    [InlineData("St-Germain", "New York")]
    public void GetDepletionStats_KnownBrandRegion_ReturnsExpectedStructure(string brand, string region)
    {
        var result = BacardiSimulatedData.GetDepletionStats(brand, region, "YTD");
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
        var result = BacardiSimulatedData.GetDepletionStats("Unknown Brand", "Florida", "YTD");
        var json = JsonSerializer.Serialize(result);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("error").GetString().Should().Contain("No data found");
        root.GetProperty("available_brands").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("available_regions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("Patrón Silver")]
    [InlineData("Angel's Envy")]
    [InlineData("Bacardi Superior")]
    [InlineData("Grey Goose")]
    [InlineData("Bombay Sapphire")]
    [InlineData("Cazadores Blanco")]
    [InlineData("Dewar's 12")]
    [InlineData("St-Germain")]
    public void GetDepletionStats_AllDocumentedBrands_ReturnDataForAtLeastOneRegion(string brand)
    {
        var regions = new[] { "Florida", "Texas", "California", "New York", "Illinois", "Georgia", "National" };
        var found = false;

        foreach (var region in regions)
        {
            var result = BacardiSimulatedData.GetDepletionStats(brand, region, "YTD");
            var json = JsonSerializer.Serialize(result);
            if (!json.Contains("error"))
            {
                found = true;
                break;
            }
        }

        found.Should().BeTrue($"brand '{brand}' should have data for at least one region");
    }

    [Theory]
    [InlineData("patrón silver", "florida")]
    [InlineData("PATRÓN SILVER", "FLORIDA")]
    [InlineData("Patrón Silver", "florida")]
    public void GetDepletionStats_CaseInsensitive_ReturnsData(string brand, string region)
    {
        var result = BacardiSimulatedData.GetDepletionStats(brand, region, "YTD");
        var json = JsonSerializer.Serialize(result);

        json.Should().NotContain("error");
    }

    [Fact]
    public void GetDepletionStats_DifferentPeriods_ProduceDifferentNumbers()
    {
        var q1 = JsonSerializer.Serialize(BacardiSimulatedData.GetDepletionStats("Patrón Silver", "Florida", "Q1"));
        var q4 = JsonSerializer.Serialize(BacardiSimulatedData.GetDepletionStats("Patrón Silver", "Florida", "Q4"));

        q1.Should().NotBe(q4, "different periods should produce different metrics");
    }

    [Theory]
    [InlineData("Patrón Silver", "Florida")]
    [InlineData("Angel's Envy", "New York")]
    [InlineData("Grey Goose", "California")]
    public void GetFieldSentiment_KnownBrandRegion_ReturnsSentimentData(string brand, string region)
    {
        var result = BacardiSimulatedData.GetFieldSentiment(brand, region);
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
        var result = BacardiSimulatedData.GetFieldSentiment("Nonexistent", "Florida");
        var json = JsonSerializer.Serialize(result);

        json.Should().Contain("error");
        json.Should().Contain("No sentiment data");
    }

    [Theory]
    [InlineData("Patrón Silver", "Florida")]
    [InlineData("Patrón Silver", "Texas")]
    public void GetFieldSentiment_DifferentRegions_ReturnDifferentSentiment(string brand, string region1, string region2 = "Texas")
    {
        // Use region1 for first call
        var result1 = JsonSerializer.Serialize(BacardiSimulatedData.GetFieldSentiment(brand, region1));
        var result2 = JsonSerializer.Serialize(BacardiSimulatedData.GetFieldSentiment(brand, region2));

        if (region1 != region2)
            result1.Should().NotBe(result2, "different regions should have different sentiment");
    }

    [Fact]
    public void GetDepletionStats_PartialMatch_FindsBrand()
    {
        // "Silver" should partially match "Patrón Silver" via Contains
        var result = BacardiSimulatedData.GetDepletionStats("Silver", "Florida", "YTD");
        var json = JsonSerializer.Serialize(result);

        json.Should().NotContain("\"error\"");
    }
}
