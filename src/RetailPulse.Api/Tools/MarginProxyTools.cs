using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class MarginByBrandTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarginByBrandTool>? _logger;

    public MarginByBrandTool(HttpClient httpClient, ILogger<MarginByBrandTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get P&L and margin analysis for a brand including revenue, COGS, gross margin, marketing, distribution, and net margin.")]
    public async Task<string> GetMarginByBrand(
        [Description("Brand name (required, e.g. 'Sierra Gold Tequila')")] string brandId,
        [Description("Period to filter (e.g. '2026-Q1'). Omit for latest.")] string? period = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/margin/{Uri.EscapeDataString(brandId)}?";
            if (!string.IsNullOrWhiteSpace(period)) url += $"&period={Uri.EscapeDataString(period)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MarginByBrandTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Margin data unavailable — MCP server not reachable." });
        }
    }
}

public class MarginDriversTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarginDriversTool>? _logger;

    public MarginDriversTool(HttpClient httpClient, ILogger<MarginDriversTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get margin drivers showing what's pushing margin up or down for a brand.")]
    public async Task<string> GetMarginDrivers(
        [Description("Brand name (required)")] string brandId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/margin/drivers/{Uri.EscapeDataString(brandId)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MarginDriversTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Margin drivers data unavailable — MCP server not reachable." });
        }
    }
}

public class MarginTrendTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MarginTrendTool>? _logger;

    public MarginTrendTool(HttpClient httpClient, ILogger<MarginTrendTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get quarterly margin trajectory for a brand showing how margins evolved over time.")]
    public async Task<string> GetMarginTrend(
        [Description("Brand name (required)")] string brandId,
        [Description("Number of quarters (1-8). Default: 4")] int quarters = 4,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"/api/margin/trend/{Uri.EscapeDataString(brandId)}?quarters={quarters}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MarginTrendTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Margin trend data unavailable — MCP server not reachable." });
        }
    }
}

public class DetectMarginRisksTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DetectMarginRisksTool>? _logger;

    public DetectMarginRisksTool(HttpClient httpClient, ILogger<DetectMarginRisksTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Detect margin risk patterns: over-promotion, shrinking base price, rising costs, thin margins.")]
    public async Task<string> DetectMarginRisks(
        [Description("Brand name. Omit to scan all brands.")] string? brandId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = "/api/margin/risks?";
            if (!string.IsNullOrWhiteSpace(brandId)) url += $"&brandId={Uri.EscapeDataString(brandId)}";
            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DetectMarginRisksTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Margin risk detection unavailable — MCP server not reachable." });
        }
    }
}
