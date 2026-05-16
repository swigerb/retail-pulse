using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class StorePerformanceTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StorePerformanceTool>? _logger;

    public StorePerformanceTool(HttpClient httpClient, ILogger<StorePerformanceTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get store performance metrics with revenue/target ratio and underperformer detection. Filterable by region.")]
    public async Task<string> GetStorePerformance(
        [Description("Region to filter (e.g. 'Northeast'). Omit for all regions.")] string? region = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = "/api/stores/performance?";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "StorePerformanceTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Store performance data unavailable — MCP server not reachable.", stores = Array.Empty<object>() });
        }
    }
}

public class ShelfLayoutTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ShelfLayoutTool>? _logger;

    public ShelfLayoutTool(HttpClient httpClient, ILogger<ShelfLayoutTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get current shelf layout/planogram for a store aisle.")]
    public async Task<string> GetShelfLayout(
        [Description("Store ID (required, e.g. 'STR-NE-001')")] string storeId,
        [Description("Aisle ID (required, e.g. 'A-Spirits')")] string aisleId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ShelfLayoutTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Shelf layout data unavailable — MCP server not reachable." });
        }
    }
}

public class OptimizePlanogramTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OptimizePlanogramTool>? _logger;

    public OptimizePlanogramTool(HttpClient httpClient, ILogger<OptimizePlanogramTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Generate optimized shelf layout with high-velocity SKUs at eye level, brand blocking, and velocity-proportional facing.")]
    public async Task<string> OptimizePlanogram(
        [Description("Store ID (required)")] string storeId,
        [Description("Aisle ID (required)")] string aisleId,
        [Description("Brand to prioritize. Omit for balanced optimization.")] string? brandFocus = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/planogram/{Uri.EscapeDataString(aisleId)}/optimize?";
            if (!string.IsNullOrWhiteSpace(brandFocus)) url += $"&brandFocus={Uri.EscapeDataString(brandFocus)}";
            HttpResponseMessage response = await _httpClient.PostAsync(url, null, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OptimizePlanogramTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Planogram optimization unavailable — MCP server not reachable." });
        }
    }
}

public class PredictStockoutTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PredictStockoutTool>? _logger;

    public PredictStockoutTool(HttpClient httpClient, ILogger<PredictStockoutTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Predict days until stockout for SKUs based on velocity vs stock. Returns risk levels: critical, warning, monitor, healthy.")]
    public async Task<string> PredictStockout(
        [Description("Store ID (required)")] string storeId,
        [Description("SKU ID to check. Omit for all SKUs.")] string? skuId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/stores/{Uri.EscapeDataString(storeId)}/stockout-risk?";
            if (!string.IsNullOrWhiteSpace(skuId)) url += $"&skuId={Uri.EscapeDataString(skuId)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PredictStockoutTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Stockout prediction unavailable — MCP server not reachable." });
        }
    }
}
