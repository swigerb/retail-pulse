using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class InventoryLevelsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryLevelsTool>? _logger;

    public InventoryLevelsTool(HttpClient httpClient, ILogger<InventoryLevelsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get current inventory levels by brand, region, category, and status. Returns SKU-level stock, safety stock, days of supply, and status.")]
    public async Task<string> GetInventoryLevels(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Category to filter. Omit for all categories.")] string? category = null,
        [Description("Status filter: 'healthy', 'low', 'critical', 'out_of_stock'. Omit for all.")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "/api/supply/inventory?";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
            if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "InventoryLevelsTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Inventory data unavailable — MCP server not reachable.", items = Array.Empty<object>() });
        }
    }
}

public class SupplyDisruptionsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SupplyDisruptionsTool>? _logger;

    public SupplyDisruptionsTool(HttpClient httpClient, ILogger<SupplyDisruptionsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get active supply chain disruptions with type, severity, impacted SKUs, and estimated resolution.")]
    public async Task<string> GetSupplyDisruptions(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Severity filter: 'high', 'medium', 'low'. Omit for all.")] string? severity = null,
        [Description("Show only active disruptions (default: true).")] bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/supply/disruptions?activeOnly={activeOnly}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(severity)) url += $"&severity={Uri.EscapeDataString(severity)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SupplyDisruptionsTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Disruption data unavailable — MCP server not reachable.", disruptions = Array.Empty<object>() });
        }
    }
}

public class FulfillmentRateTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FulfillmentRateTool>? _logger;

    public FulfillmentRateTool(HttpClient httpClient, ILogger<FulfillmentRateTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get order fulfillment rate trends. Returns fill rate %, on-time rate %, and backorder counts over time.")]
    public async Task<string> GetFulfillmentRate(
        [Description("Brand name to filter. Omit for all brands.")] string? brand = null,
        [Description("Region to filter. Omit for all regions.")] string? region = null,
        [Description("Specific period to filter (e.g. '2026-04'). Omit for all.")] string? period = null,
        [Description("Minimum number of periods to return (1-12). Default: 6")] int minPeriods = 6,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/supply/fulfillment?minPeriods={minPeriods}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FulfillmentRateTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Fulfillment data unavailable — MCP server not reachable.", rates = Array.Empty<object>() });
        }
    }
}

public class SupplyHealthTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SupplyHealthTool>? _logger;

    public SupplyHealthTool(HttpClient httpClient, ILogger<SupplyHealthTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get aggregate supply chain health summary combining inventory, disruptions, and fulfillment into an overall status (Green/Yellow/Red).")]
    public async Task<string> GetSupplyHealthSummary(
        [Description("Brand name (required)")] string brand,
        [Description("Region to scope. Omit for all regions.")] string? region = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/supply/health?brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SupplyHealthTool failed for {Brand} — returning fallback", brand);
            return JsonSerializer.Serialize(new { error = "Supply health summary unavailable — MCP server not reachable.", brand, overall_status = "unknown" });
        }
    }
}
