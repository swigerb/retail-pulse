using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class DetectThreatsTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DetectThreatsTool>? _logger;

    public DetectThreatsTool(HttpClient httpClient, ILogger<DetectThreatsTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Identify competitive threats: price drops >10%, market share losses >2 points, and high-impact competitor activities. Returns threats ranked by severity with defensive recommendations (MATCH, DIFFERENTIATE, PREEMPT, IGNORE).")]
    public async Task<string> DetectThreats(
        [Description("Brand name to scope threats. Omit for all brands.")] string? brand = null,
        [Description("Category to scope threats. Omit for all categories.")] string? category = null,
        [Description("Region to scope threats. Omit for all regions.")] string? region = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = "/api/competitive/threats?";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DetectThreatsTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Threat detection unavailable — MCP server not reachable.", threats = Array.Empty<object>() });
        }
    }
}
