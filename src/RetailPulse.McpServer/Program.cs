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

app.Run();
