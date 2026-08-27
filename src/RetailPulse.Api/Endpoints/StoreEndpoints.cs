namespace RetailPulse.Api.Endpoints;

public static class StoreEndpoints
{
    public static WebApplication MapStoreEndpoints(this WebApplication app)
    {
        // Portfolio-wide stockout risks for the Store Operations panel.
        //
        // The panel previously rendered a hardcoded three-item array declared inline
        // in Dashboard.tsx. The real signal lives in the MCP inventory feed, which
        // carries stock levels, days of supply and a status per SKU — this projects
        // the at-risk subset into the StockoutRisk shape the panel already declares,
        // so the cards show what the system actually knows.
        app.MapGet("/api/stores/stockout-risks", async (
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct,
            string? region = null,
            int limit = 20) =>
        {
            try
            {
                HttpClient client = httpFactory.CreateClient("McpServer");
                string url = "/api/supply/inventory?";
                if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

                HttpResponseMessage response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                using System.Text.Json.JsonDocument doc =
                    await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)
                    || items.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return Results.Ok(Array.Empty<StockoutRisk>());
                }

                var risks = new List<StockoutRisk>();
                foreach (System.Text.Json.JsonElement row in items.EnumerateArray())
                {
                    string status = ReadString(row, "status");
                    double daysOfSupply = ReadDouble(row, "days_of_supply");

                    // Only surface SKUs that are actually in trouble. Everything else is
                    // noise on a panel whose whole purpose is "what needs attention".
                    bool atRisk = status is "out_of_stock" or "critical" or "low"
                        || (daysOfSupply > 0 && daysOfSupply <= 10);
                    if (!atRisk) continue;

                    double safetyStock = ReadDouble(row, "safety_stock");
                    double currentStock = ReadDouble(row, "current_stock");

                    // Inventory reports stock and cover, not velocity. Derive the implied
                    // daily draw from them so the card can show a real units/day figure
                    // rather than an invented constant.
                    double velocity = daysOfSupply > 0
                        ? Math.Round(currentStock / daysOfSupply, 1)
                        : Math.Round(safetyStock / 30d, 1);

                    risks.Add(new StockoutRisk(
                        ReadString(row, "sku"),
                        $"{ReadString(row, "brand")} — {ReadString(row, "category")}",
                        ReadString(row, "brand"),
                        ReadString(row, "region"),
                        velocity,
                        daysOfSupply,
                        // Replenish back above safety stock plus a fortnight of cover.
                        (int)Math.Max(50, Math.Round((safetyStock + (velocity * 14)) / 50d) * 50)));
                }

                // Order by urgency before truncating. Taking the first N in feed order
                // returned twelve identical zero-day outages and hid the critical and
                // low-cover SKUs that are the ones still worth acting on.
                return Results.Ok(risks
                    .OrderBy(r => r.DaysRemaining)
                    .ThenByDescending(r => r.CurrentVelocity)
                    .Take(Math.Clamp(limit, 1, 50))
                    .ToList());
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(typeof(StoreEndpoints))
                    .LogWarning(ex, "Stockout risks unavailable from the MCP server.");
                return Results.Ok(Array.Empty<StockoutRisk>());
            }
        })
        .WithName("GetStockoutRisks").RequireAuthorization().RequireRateLimiting("relaxed");

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
    private static string ReadString(System.Text.Json.JsonElement row, string name) =>
        row.TryGetProperty(name, out System.Text.Json.JsonElement v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

    private static double ReadDouble(System.Text.Json.JsonElement row, string name) =>
        row.TryGetProperty(name, out System.Text.Json.JsonElement v)
            && v.ValueKind == System.Text.Json.JsonValueKind.Number
            && v.TryGetDouble(out double d)
                ? d
                : 0d;
}

/// <summary>Portfolio-wide stockout risk row for the Store Operations panel.</summary>
record StockoutRisk(
    string SkuId,
    string SkuName,
    string Brand,
    string Region,
    double CurrentVelocity,
    double DaysRemaining,
    int RecommendedReorder);
