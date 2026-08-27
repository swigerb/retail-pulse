using System.Text.Json;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Competitive-intelligence read endpoints for the SPA.
///
/// <para>
/// The competitive dashboard has always called <c>/api/competitive/*</c> on the API
/// origin, but those routes only ever existed on <c>RetailPulse.McpServer</c> — which
/// is a server-to-server dependency and is not reachable from a browser (and is now
/// on an internal ingress by design). Every request 404'd, and because the SPA client
/// treats 404 as "no data", the dashboard rendered permanently empty instead of
/// failing loudly. The feature has therefore never worked in a deployed environment.
/// </para>
///
/// <para>
/// These endpoints close that gap: the API fetches from the MCP server over the
/// existing authenticated named client and reshapes the payload into the flat arrays
/// the SPA's TypeScript contracts declare. The MCP responses are envelopes
/// (<c>{ filters, total_records, share_data }</c>) whereas the SPA expects the inner
/// collection, so the projection happens here rather than leaking the envelope into
/// the view layer.
/// </para>
/// </summary>
public static class CompetitiveEndpoints
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public static WebApplication MapCompetitiveEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/competitive")
            .WithTags("Competitive")
            .RequireAuthorization()
            .RequireRateLimiting("relaxed");

        group.MapGet("/pricing", async (
            IHttpClientFactory factory,
            ILoggerFactory loggerFactory,
            string? brand,
            string? category,
            string? region,
            CancellationToken ct) =>
        {
            JsonElement? root = await FetchAsync(
                factory, loggerFactory, "pricing", BuildQuery(brand, category, region), ct);

            return root is null
                ? Results.Ok(Array.Empty<object>())
                : Results.Ok(Project(root.Value, "pricing_data", row => new
                {
                    competitor = GetString(row, "competitor"),
                    sku = GetString(row, "brand"),
                    category = GetString(row, "category"),
                    currentPrice = GetDouble(row, "price"),
                    previousPrice = GetDouble(row, "previous_price"),
                    changePercent = GetDouble(row, "price_change_percent"),
                    priceHistory = Array.Empty<object>(),
                }));
        })
        .WithName("GetCompetitorPricing");

        group.MapGet("/market-share", async (
            IHttpClientFactory factory,
            ILoggerFactory loggerFactory,
            string? brand,
            string? category,
            string? region,
            CancellationToken ct) =>
        {
            JsonElement? root = await FetchAsync(
                factory, loggerFactory, "market-share", BuildQuery(brand, category, region), ct);

            return root is null
                ? Results.Ok(Array.Empty<object>())
                : Results.Ok(Project(root.Value, "share_data", row => new
                {
                    quarter = GetString(row, "period"),
                    brand = GetString(row, "brand"),
                    share = GetDouble(row, "share_percent"),
                    // The MCP payload carries no ownership flag. Rows sourced from our own
                    // MarketShare table have no competitor attribution, so treat a row whose
                    // source is absent as ours rather than inventing a brand allow-list here.
                    isOurBrand = string.IsNullOrWhiteSpace(GetString(row, "source")),
                }));
        })
        .WithName("GetMarketShare");

        group.MapGet("/threats", async (
            IHttpClientFactory factory,
            ILoggerFactory loggerFactory,
            string? brand,
            string? category,
            string? region,
            CancellationToken ct) =>
        {
            JsonElement? root = await FetchAsync(
                factory, loggerFactory, "threats", BuildQuery(brand, category, region), ct);
            if (root is null) return Results.Ok(Array.Empty<object>());

            int index = 0;
            return Results.Ok(Project(root.Value, "threats", row => new
            {
                id = $"{GetString(row, "type")}-{index++}",
                title = BuildThreatTitle(row),
                severity = GetString(row, "severity"),
                recommendation = GetString(row, "recommendation"),
                description = BuildThreatTitle(row),
                reasoning = GetString(row, "reasoning"),
                historicalContext = GetString(row, "historical_success_rate"),
                competitor = GetString(row, "competitor"),
                category = GetString(row, "category"),
                detectedAt = GetString(row, "detected_date"),
            }));
        })
        .WithName("DetectCompetitiveThreats");

        return app;
    }

    private static string BuildQuery(string? brand, string? category, string? region)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(brand)) parts.Add($"brand={Uri.EscapeDataString(brand)}");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(region)) parts.Add($"region={Uri.EscapeDataString(region)}");
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    /// <summary>
    /// Calls the MCP server through the shared named client (which carries the
    /// API-key header and the resilience pipeline). Returns null when the MCP
    /// server is unreachable so the caller can answer with an empty collection —
    /// a competitive dashboard with no rows is a better failure mode than a 500.
    /// </summary>
    private static async Task<JsonElement?> FetchAsync(
        IHttpClientFactory factory,
        ILoggerFactory loggerFactory,
        string path,
        string query,
        CancellationToken ct)
    {
        try
        {
            HttpClient client = factory.CreateClient("McpServer");
            HttpResponseMessage response = await client.GetAsync($"/api/competitive/{path}{query}", ct);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(CompetitiveEndpoints))
                .LogWarning(ex, "Competitive '{Path}' unavailable from the MCP server.", path);
            return null;
        }
    }

    private static List<object> Project(
        JsonElement root,
        string collectionName,
        Func<JsonElement, object> map)
    {
        if (!root.TryGetProperty(collectionName, out JsonElement collection)
            || collection.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<object>(collection.GetArrayLength());
        foreach (JsonElement row in collection.EnumerateArray())
        {
            results.Add(map(row));
        }
        return results;
    }

    private static string BuildThreatTitle(JsonElement row)
    {
        string type = GetString(row, "type");
        string competitor = GetString(row, "competitor");
        string brand = GetString(row, "brand");
        string category = GetString(row, "category");

        string subject = !string.IsNullOrWhiteSpace(competitor) ? competitor
            : !string.IsNullOrWhiteSpace(brand) ? brand
            : category;

        return type switch
        {
            "price_drop" => $"{subject} price drop in {category}",
            "share_loss" => $"{subject} share loss in {category}",
            _ => string.IsNullOrWhiteSpace(subject) ? "Competitive activity" : $"{subject} activity in {category}",
        };
    }

    private static string GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double GetDouble(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double parsed)
            ? parsed
            : 0d;
}
