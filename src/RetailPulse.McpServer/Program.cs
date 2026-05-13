using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
builder.Services.AddSingleton<ITenantProvider>(new FileTenantProvider(tenantConfigPath));

// Register SQLite-backed data store (seeds from tenant.yaml on first run)
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "data", "retailpulse.db");
builder.Services.AddSingleton<RetailPulseDb>(sp =>
    new RetailPulseDb(sp.GetRequiredService<ITenantProvider>(), dbPath, tenantConfigPath));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// MCP endpoint (SSE transport)
app.MapMcp();

// REST endpoints for direct HTTP access
app.MapGet("/api/depletion-stats", (string brand, string region, string period, RetailPulseDb data) =>
{
    var result = data.GetDepletionStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetDepletionStats");

app.MapGet("/api/portfolio-depletion-stats", (string region, RetailPulseDb data, string period = "YTD") =>
{
    var result = data.GetPortfolioDepletionStats(region, period);
    return Results.Ok(result);
})
.WithName("GetPortfolioDepletionStats");

app.MapGet("/api/field-sentiment", (string brand, string region, RetailPulseDb data) =>
{
    var result = data.GetFieldSentiment(brand, region);
    return Results.Ok(result);
})
.WithName("GetFieldSentiment");

app.MapGet("/api/shipment-stats", (string brand, string region, string period, RetailPulseDb data) =>
{
    var result = data.GetShipmentStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetShipmentStats");

app.MapGet("/api/variant-mix", (string brand, RetailPulseDb data, string region = "National") =>
{
    var result = data.GetVariantMix(brand, region);
    return Results.Ok(result);
})
.WithName("GetVariantMix");

app.MapGet("/api/historical-demand", (string brand, RetailPulseDb data, string region = "National", string channel = "All") =>
{
    var effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    var effectiveChannel = string.Equals(channel, "All", StringComparison.OrdinalIgnoreCase) ? null : channel;
    var result = data.GetHistoricalDemand(brand, effectiveRegion, effectiveChannel);
    return Results.Ok(result);
})
.WithName("GetHistoricalDemand");

app.MapGet("/api/forecast", (string brand, RetailPulseDb data, string region = "National") =>
{
    var effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    var result = data.GenerateForecast(brand, effectiveRegion);
    return Results.Ok(result);
})
.WithName("GenerateForecast");

app.MapGet("/api/seasonality-factors", (RetailPulseDb data, string category = "All") =>
{
    var effectiveCategory = string.Equals(category, "All", StringComparison.OrdinalIgnoreCase) ? null : category;
    var result = data.GetSeasonalityFactors(effectiveCategory);
    return Results.Ok(result);
})
.WithName("GetSeasonalityFactors");

app.MapGet("/api/demand-risks", (string brand, RetailPulseDb data, string region = "National") =>
{
    var effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    var result = data.IdentifyDemandRisks(brand, effectiveRegion);
    return Results.Ok(result);
})
.WithName("IdentifyDemandRisks");

app.MapGet("/api/demand/history", (RetailPulseDb data, string? brand = null, string? region = null, string? channel = null, int months = 12) =>
{
    var result = data.GetHistoricalDemand(brand, region, channel, months);
    return Results.Ok(result);
})
.WithName("GetHistoricalDemand");

app.MapGet("/api/demand/forecast", (string brand, RetailPulseDb data, string? region = null, int days = 90) =>
{
    var result = data.GenerateForecast(brand, region, days);
    return Results.Ok(result);
})
.WithName("GenerateForecast");

app.MapGet("/api/demand/seasonality", (RetailPulseDb data, string? category = null) =>
{
    var result = data.GetSeasonalityFactors(category);
    return Results.Ok(result);
})
.WithName("GetSeasonalityFactors");

app.MapGet("/api/demand/risks", (RetailPulseDb data, string? brand = null, string? region = null) =>
{
    var result = data.IdentifyDemandRisks(brand, region);
    return Results.Ok(result);
})
.WithName("IdentifyDemandRisks");

// ── Promo REST endpoints ─────────────────────────────────────────────
app.MapGet("/api/promo/history", (RetailPulseDb data, string? brand = null, string? region = null, string? promoType = null, int months = 18) =>
{
    var result = data.GetPromoHistory(brand, region, promoType, months);
    return Results.Ok(result);
})
.WithName("GetPromoHistory");

app.MapGet("/api/promo/calculate-lift", (string brand, string region, string promoType, double spend, RetailPulseDb data) =>
{
    var result = data.CalculateLift(brand, region, promoType, spend);
    return Results.Ok(result);
})
.WithName("CalculateLift");

app.MapGet("/api/promo/evaluate-timing", (string brand, string region, string startDate, string endDate, RetailPulseDb data) =>
{
    if (!DateOnly.TryParse(startDate, out var start) || !DateOnly.TryParse(endDate, out var end))
        return Results.BadRequest(new { error = "Invalid date format. Use ISO format (yyyy-MM-dd)." });
    var result = data.EvaluateTiming(brand, region, start, end);
    return Results.Ok(result);
})
.WithName("EvaluateTiming");

app.MapGet("/api/promo/estimate-roi", (string brand, string region, string promoType, double spend, int durationWeeks, RetailPulseDb data) =>
{
    var result = data.EstimateROI(brand, region, promoType, spend, durationWeeks);
    return Results.Ok(result);
})
.WithName("EstimateROI");

app.MapGet("/api/promo/calendar", (RetailPulseDb data, string? brand = null, string? region = null, int months = 6) =>
{
    var result = data.GetPromoCalendar(brand, region, months);
    return Results.Ok(result);
})
.WithName("GetPromoCalendar");

app.MapGet("/api/promo/types", (RetailPulseDb data) =>
{
    var result = RetailPulseDb.GetPromoTypes();
    return Results.Ok(result);
})
.WithName("GetPromoTypes");

// ── Competitive Intelligence REST endpoints ──────────────────────────
app.MapGet("/api/competitive/pricing", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null, string? competitors = null) =>
{
    var result = data.GetCompetitorPricing(brand, category, region, competitors);
    return Results.Ok(result);
})
.WithName("GetCompetitorPricing");

app.MapGet("/api/competitive/market-share", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null, string? period = null) =>
{
    var result = data.GetMarketShare(brand, category, region, period);
    return Results.Ok(result);
})
.WithName("GetMarketShare");

app.MapGet("/api/competitive/threats", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null) =>
{
    var result = data.DetectCompetitiveThreats(brand, category, region);
    return Results.Ok(result);
})
.WithName("DetectCompetitiveThreats");

app.MapGet("/api/competitive/landscape", (string category, string region, RetailPulseDb data) =>
{
    var result = data.GetCompetitiveLandscape(category, region);
    return Results.Ok(result);
})
.WithName("GetCompetitiveLandscape");

app.Run();
