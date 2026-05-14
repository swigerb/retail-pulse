namespace RetailPulse.Api.Endpoints;

public static class MarginEndpoints
{
    public static WebApplication MapMarginEndpoints(this WebApplication app)
    {
        app.MapGet("/api/margin/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct, string? period = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/margin/{Uri.EscapeDataString(brandId)}?";
            if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetMarginByBrand").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/margin/drivers/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/margin/drivers/{Uri.EscapeDataString(brandId)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetMarginDrivers").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/margin/trend/{brandId}", async (string brandId, IHttpClientFactory httpFactory, CancellationToken ct, int quarters = 4) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/margin/trend/{Uri.EscapeDataString(brandId)}?quarters={quarters}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetMarginTrend").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/margin/risks", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brandId = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = "/api/margin/risks?";
            if (!string.IsNullOrWhiteSpace(brandId)) url += $"&brandId={Uri.EscapeDataString(brandId)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("DetectMarginRisks").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}
