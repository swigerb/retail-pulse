using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using RetailPulse.Api.Prefetch;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Prefetch;

public class ToolPrefetchServiceTests
{
    private readonly ToolPrefetchService _sut;
    private readonly Mock<HttpMessageHandler> _httpHandler;

    public ToolPrefetchServiceTests()
    {
        _httpHandler = new Mock<HttpMessageHandler>();
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":\"mock\"}")
            });

        var httpClient = new HttpClient(_httpHandler.Object) { BaseAddress = new Uri("http://localhost") };

#pragma warning disable CS0618
        var historicalDemandTool = new HistoricalDemandTool(httpClient);
        var seasonalityTool = new SeasonalityFactorsTool(httpClient);
#pragma warning restore CS0618

        _sut = new ToolPrefetchService(
            historicalDemandTool,
            seasonalityTool,
            NullLogger<ToolPrefetchService>.Instance);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Entity Extraction Tests
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("How is Sierra Gold Tequila performing?", "Sierra Gold Tequila")]
    [InlineData("What's the demand for Apex Grill in Q4?", "Apex Grill")]
    [InlineData("Show me Ridgeline Bourbon trends", "Ridgeline Bourbon")]
    [InlineData("Coastal Creamery outlook for next month", "Coastal Creamery")]
    [InlineData("Urban Roast Coffee is our top performer", "Urban Roast Coffee")]
    [InlineData("Mountain Trail Granola sales breakdown", "Mountain Trail Granola")]
    public void ExtractEntities_DetectsBrand(string message, string expectedBrand)
    {
        var entities = _sut.ExtractEntities(message);
        Assert.Equal(expectedBrand, entities.Brand);
    }

    [Theory]
    [InlineData("How is demand in the Southwest?", "Southwest")]
    [InlineData("Northeast performance this quarter", "Northeast")]
    [InlineData("West Coast channel analysis", "West Coast")]
    [InlineData("Pacific Northwest growth", "Pacific Northwest")]
    public void ExtractEntities_DetectsRegion(string message, string expectedRegion)
    {
        var entities = _sut.ExtractEntities(message);
        Assert.Equal(expectedRegion, entities.Region);
    }

    [Theory]
    [InlineData("On-Premise channel performance", "On-Premise")]
    [InlineData("How's the Off-Premise doing?", "Off-Premise")]
    [InlineData("E-Commerce sales are growing", "E-Commerce")]
    public void ExtractEntities_DetectsChannel(string message, string expectedChannel)
    {
        var entities = _sut.ExtractEntities(message);
        Assert.Equal(expectedChannel, entities.Channel);
    }

    [Theory]
    [InlineData("Sierra Gold Tequila demand", "Spirits")]
    [InlineData("Apex Grill growth this quarter", "Quick-Serve Restaurant")]
    [InlineData("FreshMart weekly trends", "Grocery")]
    [InlineData("Pinnacle Hardware forecast", "Home Improvement")]
    public void ExtractEntities_DerivesCategory_FromBrand(string message, string expectedCategory)
    {
        var entities = _sut.ExtractEntities(message);
        Assert.Equal(expectedCategory, entities.Category);
    }

    [Fact]
    public void ExtractEntities_DetectsExplicitCategory_WhenNoBrand()
    {
        var entities = _sut.ExtractEntities("What are the Spirits category trends?");
        Assert.Null(entities.Brand);
        Assert.Equal("Spirits", entities.Category);
    }

    [Fact]
    public void ExtractEntities_ExtractsMultipleEntities()
    {
        var entities = _sut.ExtractEntities(
            "How is Sierra Gold Tequila performing in the Southwest On-Premise channel?");

        Assert.Equal("Sierra Gold Tequila", entities.Brand);
        Assert.Equal("Southwest", entities.Region);
        Assert.Equal("On-Premise", entities.Channel);
        Assert.Equal("Spirits", entities.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hello world, what's up?")]
    public void ExtractEntities_ReturnsEmpty_WhenNoEntitiesFound(string message)
    {
        var entities = _sut.ExtractEntities(message);
        Assert.False(entities.HasAny);
    }

    [Fact]
    public void ExtractEntities_IsCaseInsensitive()
    {
        var entities = _sut.ExtractEntities("SIERRA GOLD TEQUILA in the SOUTHWEST");
        Assert.Equal("Sierra Gold Tequila", entities.Brand);
        Assert.Equal("Southwest", entities.Region);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Prefetch Tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrefetchAsync_ReturnsDemandData_WhenBrandExtracted()
    {
        var entities = new PrefetchEntities("Sierra Gold Tequila", "Southwest", null, "Spirits");

        var results = await _sut.PrefetchAsync(AgentIntent.DemandForecasting, entities);

        Assert.NotEmpty(results);
        Assert.True(results.ContainsKey("GetHistoricalDemand"));
        Assert.True(results.ContainsKey("GetSeasonalityFactors"));
    }

    [Fact]
    public async Task PrefetchAsync_ReturnsSeasonalityOnly_WhenOnlyCategoryAvailable()
    {
        var entities = new PrefetchEntities(null, null, null, "Spirits");

        var results = await _sut.PrefetchAsync(AgentIntent.DemandForecasting, entities);

        Assert.Single(results);
        Assert.True(results.ContainsKey("GetSeasonalityFactors"));
    }

    [Fact]
    public async Task PrefetchAsync_ReturnsEmpty_WhenNoEntities()
    {
        var results = await _sut.PrefetchAsync(AgentIntent.DemandForecasting, PrefetchEntities.Empty);

        Assert.Empty(results);
    }

    [Fact]
    public async Task PrefetchAsync_ReturnsEmpty_ForUnsupportedIntent()
    {
        var entities = new PrefetchEntities("Sierra Gold Tequila", null, null, "Spirits");

        var results = await _sut.PrefetchAsync(AgentIntent.SupplyShipments, entities);

        Assert.Empty(results);
    }

    [Fact]
    public async Task PrefetchAsync_ReturnsPartialResults_WhenOneToolFails()
    {
        // Set up handler to fail only for seasonality calls
        var callCount = 0;
        _httpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 2
                    ? throw new HttpRequestException("Connection refused")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":\"ok\"}")
                    };
            });

        var entities = new PrefetchEntities("Sierra Gold Tequila", null, null, "Spirits");

        var results = await _sut.PrefetchAsync(AgentIntent.DemandForecasting, entities);

        // Should have at least partial results (the tool that didn't fail returns a fallback JSON)
        Assert.True(results.Count >= 1);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BuildSystemPromptWithPrefetch Tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSystemPromptWithPrefetch_ReturnsOriginal_WhenNullData()
    {
        var original = "You are a demand forecasting agent.";
        var result = RetailPulse.Api.Agents.AgentExecutionPipeline.BuildSystemPromptWithPrefetch(original, null);
        Assert.Equal(original, result);
    }

    [Fact]
    public void BuildSystemPromptWithPrefetch_ReturnsOriginal_WhenEmptyData()
    {
        var original = "You are a demand forecasting agent.";
        var result = RetailPulse.Api.Agents.AgentExecutionPipeline.BuildSystemPromptWithPrefetch(
            original, new Dictionary<string, string>());
        Assert.Equal(original, result);
    }

    [Fact]
    public void BuildSystemPromptWithPrefetch_AppendsData()
    {
        var original = "You are a demand forecasting agent.";
        var data = new Dictionary<string, string>
        {
            ["GetHistoricalDemand"] = "{\"brand\":\"Sierra Gold\",\"data\":[]}",
            ["GetSeasonalityFactors"] = "{\"category\":\"Spirits\",\"factors\":[]}"
        };

        var result = RetailPulse.Api.Agents.AgentExecutionPipeline.BuildSystemPromptWithPrefetch(original, data);

        Assert.Contains("## Pre-loaded Data", result);
        Assert.Contains("### GetHistoricalDemand", result);
        Assert.Contains("### GetSeasonalityFactors", result);
        Assert.Contains("Sierra Gold", result);
        Assert.Contains("do NOT call these tools again", result);
        Assert.StartsWith(original, result);
    }
}
