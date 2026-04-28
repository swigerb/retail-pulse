using RetailPulse.McpServer.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

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
app.MapGet("/api/depletion-stats", (string brand, string region, string period) =>
{
    var data = BacardiSimulatedData.GetDepletionStats(brand, region, period);
    return Results.Ok(data);
})
.WithName("GetDepletionStats");

app.MapGet("/api/field-sentiment", (string brand, string region) =>
{
    var data = BacardiSimulatedData.GetFieldSentiment(brand, region);
    return Results.Ok(data);
})
.WithName("GetFieldSentiment");

app.MapGet("/api/shipment-stats", (string brand, string region, string period) =>
{
    var data = BacardiSimulatedData.GetShipmentStats(brand, region, period);
    return Results.Ok(data);
})
.WithName("GetShipmentStats");

app.Run();
