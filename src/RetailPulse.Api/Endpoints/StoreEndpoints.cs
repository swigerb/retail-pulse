namespace RetailPulse.Api.Endpoints;

public static class StoreEndpoints
{
    public static WebApplication MapStoreEndpoints(this WebApplication app)
    {
        app.MapGet("/api/stores/performance", async (IHttpClientFactory httpFactory, CancellationToken ct, string? region = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = "/api/stores/performance?";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetStorePerformance").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/stores/{storeId}/planogram/{aisleId}", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetShelfLayout").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/stores/{storeId}/planogram/{aisleId}/optimize", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct, string? brandFocus = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}/optimize?";
            if (!string.IsNullOrWhiteSpace(brandFocus)) url += $"&brandFocus={Uri.EscapeDataString(brandFocus)}";

            var response = await client.PostAsync(url, null, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("OptimizePlanogram").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapGet("/api/stores/{storeId}/stockout-risk", async (string storeId, IHttpClientFactory httpFactory, CancellationToken ct, string? skuId = null) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/stores/{Uri.EscapeDataString(storeId)}/stockout-risk?";
            if (!string.IsNullOrWhiteSpace(skuId)) url += $"&skuId={Uri.EscapeDataString(skuId)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("PredictStockout").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}
