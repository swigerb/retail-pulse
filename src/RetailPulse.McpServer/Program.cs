using RetailPulse.Contracts;
using RetailPulse.McpServer.Data;
using RetailPulse.McpServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Load tenant configuration
var tenantConfigPath = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tenant.yaml");
builder.Services.AddSingleton<ITenantProvider>(new FileTenantProvider(tenantConfigPath));

// Register tenant-driven simulated data as a singleton
builder.Services.AddSingleton<SimulatedMetricsData>(sp =>
    new SimulatedMetricsData(sp.GetRequiredService<ITenantProvider>()));

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
app.MapGet("/api/depletion-stats", (string brand, string region, string period, SimulatedMetricsData data) =>
{
    var result = data.GetDepletionStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetDepletionStats");

app.MapGet("/api/field-sentiment", (string brand, string region, SimulatedMetricsData data) =>
{
    var result = data.GetFieldSentiment(brand, region);
    return Results.Ok(result);
})
.WithName("GetFieldSentiment");

app.MapGet("/api/shipment-stats", (string brand, string region, string period, SimulatedMetricsData data) =>
{
    var result = data.GetShipmentStats(brand, region, period);
    return Results.Ok(result);
})
.WithName("GetShipmentStats");

app.Run();
