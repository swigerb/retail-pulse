using System.Security.Cryptography;
using System.Text;
using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Load tenant configuration
string tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
builder.Services.AddSingleton<ITenantProvider>(new FileTenantProvider(tenantConfigPath));

// Register SQLite-backed data store (seeds from tenant.yaml on first run)
string dbPath = Path.Combine(builder.Environment.ContentRootPath, "data", "retailpulse.db");
builder.Services.AddSingleton(sp =>
    new RetailPulseDb(sp.GetRequiredService<ITenantProvider>(), dbPath, tenantConfigPath));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// API key gate for REST + MCP endpoints. Enforced outside Development whenever
// an ApiKey:Value is configured. Comparison uses CryptographicOperations.FixedTimeEquals
// to avoid leaking the secret through string-equality timing differences.
string? expectedApiKey = builder.Configuration["ApiKey:Value"];
bool apiKeyRequired = !app.Environment.IsDevelopment()
    || builder.Configuration.GetValue("ApiKey:Enabled", false);
byte[]? expectedKeyBytes = string.IsNullOrWhiteSpace(expectedApiKey)
    ? null
    : Encoding.UTF8.GetBytes(expectedApiKey);
string apiKeyHeader = builder.Configuration["ApiKey:Header"] ?? "X-Api-Key";

if (apiKeyRequired && expectedKeyBytes is null)
{
    app.Logger.LogWarning(
        "MCP server is running in a non-Development environment without ApiKey:Value configured. " +
        "All /api and /mcp requests will be rejected.");
}

app.Use(async (context, next) =>
{
    PathString path = context.Request.Path;
    bool needsAuth = apiKeyRequired
        && (path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp"));

    if (!needsAuth)
    {
        await next(context);
        return;
    }

    if (expectedKeyBytes is null)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("API key gate is enabled but no key is configured.");
        return;
    }

    if (!context.Request.Headers.TryGetValue(apiKeyHeader, out Microsoft.Extensions.Primitives.StringValues provided)
        || !ApiKeyMatches(provided.ToString(), expectedKeyBytes))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Missing or invalid API key.");
        return;
    }

    await next(context);
});

// MCP endpoint (SSE transport)
app.MapMcp();

// REST endpoints for direct HTTP access
app.MapGet("/api/depletion-stats", (string brand, string region, string period, RetailPulseDb data) =>
{
    object result = data.GetDepletionStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetDepletionStats");

app.MapGet("/api/portfolio-depletion-stats", (string region, RetailPulseDb data, string period = "YTD") =>
{
    object result = data.GetPortfolioDepletionStats(region, period);
    return Results.Ok(result);
})
.WithName("GetPortfolioDepletionStats");

app.MapGet("/api/field-sentiment", (string brand, string region, RetailPulseDb data) =>
{
    object result = data.GetFieldSentiment(brand, region);
    return Results.Ok(result);
})
.WithName("GetFieldSentiment");

app.MapGet("/api/shipment-stats", (string brand, string region, string period, RetailPulseDb data) =>
{
    object result = data.GetShipmentStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetShipmentStats");

app.MapGet("/api/variant-mix", (string brand, RetailPulseDb data, string region = "National") =>
{
    object result = data.GetVariantMix(brand, region);
    return Results.Ok(result);
})
.WithName("GetVariantMix");

// ── Legacy demand routes (deprecated — use /api/demand/* instead) ────
app.MapGet("/api/historical-demand", (HttpContext ctx, string brand, RetailPulseDb data, string region = "National", string channel = "All") =>
{
    ctx.Response.Headers["X-Deprecated"] = "true";
    ctx.Response.Headers["Sunset"] = "2026-12-31";
    string? effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    string? effectiveChannel = string.Equals(channel, "All", StringComparison.OrdinalIgnoreCase) ? null : channel;
    object result = data.GetHistoricalDemand(brand, effectiveRegion, effectiveChannel);
    return Results.Ok(result);
})
.WithName("GetHistoricalDemand_Legacy");

app.MapGet("/api/forecast", (HttpContext ctx, string brand, RetailPulseDb data, string region = "National") =>
{
    ctx.Response.Headers["X-Deprecated"] = "true";
    ctx.Response.Headers["Sunset"] = "2026-12-31";
    string? effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    object result = data.GenerateForecast(brand, effectiveRegion);
    return Results.Ok(result);
})
.WithName("GenerateForecast_Legacy");

app.MapGet("/api/seasonality-factors", (HttpContext ctx, RetailPulseDb data, string category = "All") =>
{
    ctx.Response.Headers["X-Deprecated"] = "true";
    ctx.Response.Headers["Sunset"] = "2026-12-31";
    string? effectiveCategory = string.Equals(category, "All", StringComparison.OrdinalIgnoreCase) ? null : category;
    object result = data.GetSeasonalityFactors(effectiveCategory);
    return Results.Ok(result);
})
.WithName("GetSeasonalityFactors_Legacy");

app.MapGet("/api/demand-risks", (HttpContext ctx, string brand, RetailPulseDb data, string region = "National") =>
{
    ctx.Response.Headers["X-Deprecated"] = "true";
    ctx.Response.Headers["Sunset"] = "2026-12-31";
    string? effectiveRegion = string.Equals(region, "National", StringComparison.OrdinalIgnoreCase) ? null : region;
    object result = data.IdentifyDemandRisks(brand, effectiveRegion);
    return Results.Ok(result);
})
.WithName("IdentifyDemandRisks_Legacy");

// ── Current demand routes ────────────────────────────────────────────
app.MapGet("/api/demand/history", (RetailPulseDb data, string? brand = null, string? region = null, string? channel = null, int months = 12) =>
{
    object result = data.GetHistoricalDemand(brand, region, channel, months);
    return Results.Ok(result);
})
.WithName("GetDemandHistory");

app.MapGet("/api/demand/forecast", (string brand, RetailPulseDb data, string? region = null, int days = 90) =>
{
    object result = data.GenerateForecast(brand, region, days);
    return Results.Ok(result);
})
.WithName("GetDemandForecast");

app.MapGet("/api/demand/seasonality", (RetailPulseDb data, string? category = null) =>
{
    object result = data.GetSeasonalityFactors(category);
    return Results.Ok(result);
})
.WithName("GetDemandSeasonality");

app.MapGet("/api/demand/risks", (RetailPulseDb data, string? brand = null, string? region = null) =>
{
    object result = data.IdentifyDemandRisks(brand, region);
    return Results.Ok(result);
})
.WithName("GetDemandRisks");

// ── Promo REST endpoints ─────────────────────────────────────────────
app.MapGet("/api/promo/history", (RetailPulseDb data, string? brand = null, string? region = null, string? promoType = null, int months = 18) =>
{
    object result = data.GetPromoHistory(brand, region, promoType, months);
    return Results.Ok(result);
})
.WithName("GetPromoHistory");

app.MapGet("/api/promo/calculate-lift", (string brand, string region, string promoType, double spend, RetailPulseDb data) =>
{
    object result = data.CalculateLift(brand, region, promoType, spend);
    return Results.Ok(result);
})
.WithName("CalculateLift");

app.MapGet("/api/promo/evaluate-timing", (string brand, string region, string startDate, string endDate, RetailPulseDb data) =>
{
    if (!DateOnly.TryParse(startDate, out DateOnly start) || !DateOnly.TryParse(endDate, out DateOnly end))
        return Results.BadRequest(new { error = "Invalid date format. Use ISO format (yyyy-MM-dd)." });
    object result = data.EvaluateTiming(brand, region, start, end);
    return Results.Ok(result);
})
.WithName("EvaluateTiming");

app.MapGet("/api/promo/estimate-roi", (string brand, string region, string promoType, double spend, int durationWeeks, RetailPulseDb data) =>
{
    object result = data.EstimateROI(brand, region, promoType, spend, durationWeeks);
    return Results.Ok(result);
})
.WithName("EstimateROI");

app.MapGet("/api/promo/calendar", (RetailPulseDb data, string? brand = null, string? region = null, int months = 6) =>
{
    object result = data.GetPromoCalendar(brand, region, months);
    return Results.Ok(result);
})
.WithName("GetPromoCalendar");

app.MapGet("/api/promo/types", (RetailPulseDb data) =>
{
    object result = RetailPulseDb.GetPromoTypes();
    return Results.Ok(result);
})
.WithName("GetPromoTypes");

// ── Competitive Intelligence REST endpoints ──────────────────────────
app.MapGet("/api/competitive/pricing", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null, string? competitors = null) =>
{
    object result = data.GetCompetitorPricing(brand, category, region, competitors);
    return Results.Ok(result);
})
.WithName("GetCompetitorPricing");

app.MapGet("/api/competitive/market-share", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null, string? period = null) =>
{
    object result = data.GetMarketShare(brand, category, region, period);
    return Results.Ok(result);
})
.WithName("GetMarketShare");

app.MapGet("/api/competitive/threats", (RetailPulseDb data, string? brand = null, string? category = null, string? region = null) =>
{
    object result = data.DetectCompetitiveThreats(brand, category, region);
    return Results.Ok(result);
})
.WithName("DetectCompetitiveThreats");

app.MapGet("/api/competitive/landscape", (string category, string region, RetailPulseDb data) =>
{
    object result = data.GetCompetitiveLandscape(category, region);
    return Results.Ok(result);
})
.WithName("GetCompetitiveLandscape");

// ── Supply Chain REST endpoints ──────────────────────────────────────
app.MapGet("/api/supply/inventory", (RetailPulseDb data, string? brand = null, string? region = null, string? category = null, string? status = null) =>
{
    object result = data.GetInventoryLevels(brand, region, category, status);
    return Results.Ok(result);
})
.WithName("GetInventoryLevels");

app.MapGet("/api/supply/disruptions", (RetailPulseDb data, string? brand = null, string? region = null, string? severity = null, bool activeOnly = true) =>
{
    object result = data.GetSupplyDisruptions(brand, region, severity, activeOnly);
    return Results.Ok(result);
})
.WithName("GetSupplyDisruptions");

app.MapGet("/api/supply/fulfillment", (RetailPulseDb data, string? brand = null, string? region = null, string? period = null, int minPeriods = 6) =>
{
    object result = data.GetFulfillmentRates(brand, region, period, minPeriods);
    return Results.Ok(result);
})
.WithName("GetFulfillmentRates");

app.MapGet("/api/supply/health", (string brand, RetailPulseDb data, string? region = null) =>
{
    object result = data.GetSupplyHealthSummary(brand, region);
    return Results.Ok(result);
})
.WithName("GetSupplyHealthSummary");

// ── Store Operations REST endpoints ──────────────────────────────────
app.MapGet("/api/stores/performance", (RetailPulseDb data, string? region = null, string? storeId = null) =>
{
    object result = data.GetStorePerformance(region, storeId);
    return Results.Ok(result);
})
.WithName("GetStorePerformance");

app.MapGet("/api/stores/{storeId}/planogram/{aisleId}", (string storeId, string aisleId, RetailPulseDb data) =>
{
    object result = data.GetShelfLayout(storeId, aisleId);
    return Results.Ok(result);
})
.WithName("GetShelfLayout");

app.MapPost("/api/stores/{storeId}/planogram/{aisleId}/optimize", (string storeId, string aisleId, RetailPulseDb data) =>
{
    object result = data.OptimizePlanogram(storeId, aisleId);
    return Results.Ok(result);
})
.WithName("OptimizePlanogram");

app.MapGet("/api/stores/{storeId}/stockout-risk", (string storeId, RetailPulseDb data, string? skuId = null) =>
{
    object result = data.PredictStockout(storeId, skuId);
    return Results.Ok(result);
})
.WithName("PredictStockout");

// ── Margin REST endpoints ────────────────────────────────────────────
app.MapGet("/api/margin/{brand}", (string brand, RetailPulseDb data, string? period = null) =>
{
    object result = data.GetMarginByBrand(brand, period);
    return Results.Ok(result);
})
.WithName("GetMarginByBrand");

app.MapGet("/api/margin/drivers/{brand}", (string brand, RetailPulseDb data) =>
{
    object result = data.GetMarginDrivers(brand);
    return Results.Ok(result);
})
.WithName("GetMarginDrivers");

app.MapGet("/api/margin/trend/{brand}", (string brand, RetailPulseDb data, int quarters = 4) =>
{
    object result = data.GetMarginTrend(brand, quarters);
    return Results.Ok(result);
})
.WithName("GetMarginTrend");

app.MapGet("/api/margin/risks", (RetailPulseDb data, string? brand = null) =>
{
    object result = data.DetectMarginRisks(brand);
    return Results.Ok(result);
})
.WithName("DetectMarginRisks");

app.Run();

static bool ApiKeyMatches(string provided, byte[] expectedBytes)
{
    if (string.IsNullOrEmpty(provided))
        return false;

    byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
    return providedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}
