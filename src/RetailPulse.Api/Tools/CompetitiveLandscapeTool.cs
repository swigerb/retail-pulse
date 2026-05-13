using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class CompetitiveLandscapeTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CompetitiveLandscapeTool>? _logger;

    public CompetitiveLandscapeTool(HttpClient httpClient, ILogger<CompetitiveLandscapeTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get a full competitive overview for a category and region. Returns market share positions, recent competitor activities, and pricing moves for all players.")]
    public async Task<string> GetCompetitiveLandscape(
        [Description("Category (required, e.g. 'Spirits', 'Grocery', 'Furniture')")] string category,
        [Description("Region (required, e.g. 'Northeast', 'West Coast')")] string region,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(region))
                return JsonSerializer.Serialize(new { error = "Both 'category' and 'region' parameters are required." });

            var url = $"/api/competitive/landscape?category={Uri.EscapeDataString(category)}&region={Uri.EscapeDataString(region)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CompetitiveLandscapeTool failed — returning fallback");
            return JsonSerializer.Serialize(new { error = "Competitive landscape unavailable — MCP server not reachable." });
        }
    }
}
