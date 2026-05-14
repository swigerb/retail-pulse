namespace RetailPulse.Api.Endpoints;

public static class SupplyEndpoints
{
    public static WebApplication MapSupplyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/supply/health", async (string brand, IHttpClientFactory httpFactory, CancellationToken ct, string? region = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/supply/health?brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetSupplyHealth").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/supply/inventory", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? category = null, string? status = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = "/api/supply/inventory?";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
            if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetSupplyInventory").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/supply/disruptions", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? severity = null, bool activeOnly = true) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/supply/disruptions?activeOnly={activeOnly}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(severity)) url += $"&severity={Uri.EscapeDataString(severity)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetSupplyDisruptions").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/supply/fulfillment", async (IHttpClientFactory httpFactory, CancellationToken ct, string? brand = null, string? region = null, string? period = null, int minPeriods = 6) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/supply/fulfillment?minPeriods={minPeriods}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetSupplyFulfillment").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}
