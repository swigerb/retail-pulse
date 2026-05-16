namespace RetailPulse.Api.Endpoints;

public static class StoreEndpoints
{
    public static WebApplication MapStoreEndpoints(this WebApplication app)
    {
        app.MapGet("/api/stores/performance", async (IHttpClientFactory httpFactory, CancellationToken ct, string? region = null) =>
        {
            HttpClient client = httpFactory.CreateClient("McpServer");
            string url = "/api/stores/performance?";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetStorePerformance").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/stores/{storeId}/planogram/{aisleId}", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            HttpClient client = httpFactory.CreateClient("McpServer");
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetShelfLayout").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/stores/{storeId}/planogram/{aisleId}/optimize", async (string storeId, string aisleId, IHttpClientFactory httpFactory, CancellationToken ct, string? brandFocus = null) =>
        {
            HttpClient client = httpFactory.CreateClient("McpServer");
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}/optimize?";
            if (!string.IsNullOrWhiteSpace(brandFocus)) url += $"&brandFocus={Uri.EscapeDataString(brandFocus)}";

            HttpResponseMessage response = await client.PostAsync(url, null, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("OptimizePlanogram").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapGet("/api/stores/{storeId}/stockout-risk", async (string storeId, IHttpClientFactory httpFactory, CancellationToken ct, string? skuId = null) =>
        {
            HttpClient client = httpFactory.CreateClient("McpServer");
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/stockout-risk?";
            if (!string.IsNullOrWhiteSpace(skuId)) url += $"&skuId={Uri.EscapeDataString(skuId)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("PredictStockout").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}
