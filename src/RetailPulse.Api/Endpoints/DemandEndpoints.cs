using RetailPulse.Api.Security;

namespace RetailPulse.Api.Endpoints;

public static class DemandEndpoints
{
    // Thin read-only seam over the internal MCP server's GET /api/forecast, exposed as the
    // get_demand_forecast tool by the APIM MCP server (ADR 017). It deliberately does not use
    // ForecastTool, which is obsolete and converts failures into a 200 fallback body.
    public static WebApplication MapDemandEndpoints(this WebApplication app)
    {
        app.MapGet("/api/demand/forecast", async (
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct,
            string? brand = null,
            string? region = null) =>
        {
            if (string.IsNullOrWhiteSpace(brand))
            {
                return Results.BadRequest(new { error = "brand is required" });
            }

            string effectiveRegion = string.IsNullOrWhiteSpace(region) ? "National" : region;
            try
            {
                HttpClient client = httpFactory.CreateClient("McpServer");
                string url = $"/api/forecast?brand={Uri.EscapeDataString(brand)}&region={Uri.EscapeDataString(effectiveRegion)}";
                HttpResponseMessage response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(ct);
                return Results.Content(json, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                loggerFactory.CreateLogger(typeof(DemandEndpoints))
                    .LogWarning(ex, "Demand forecast unavailable from the MCP server.");
                return Results.Json(new { error = "forecast_unavailable" }, statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .WithName("GetDemandForecast").RequireAuthorization().RequireRateLimiting("relaxed")
        .AddEndpointFilter<McpGatewayResponseBudgetFilter>();

        return app;
    }
}
