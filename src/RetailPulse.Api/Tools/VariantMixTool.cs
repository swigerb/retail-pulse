using System.ComponentModel;
using System.Text.Json;

namespace RetailPulse.Api.Tools;

public class VariantMixTool
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VariantMixTool>? _logger;

    public VariantMixTool(HttpClient httpClient, ILogger<VariantMixTool>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    [Description("Get variant/SKU mix percentages and depletion performance for each variant of a brand. Use this for any query about variant mix, product mix, SKU breakdown, flavor split, or menu item distribution. Required for donut charts and pie charts showing brand variant composition.")]
    public async Task<string> GetVariantMix(
        [Description("The brand name, e.g. 'Apex Grill', 'FreshMart', 'Coastline Tacos'")] string brand,
        [Description("The region, e.g. 'Northeast', 'Southwest', 'National'. Defaults to 'National'.")] string region = "National",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/api/variant-mix?brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region))
                url += $"&region={Uri.EscapeDataString(region)}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "VariantMixTool failed for brand {Brand}/{Region} — returning fallback", brand, region);
            return JsonSerializer.Serialize(new
            {
                brand,
                region,
                error = "Variant mix data unavailable — MCP server not reachable.",
                variants = Array.Empty<object>()
            });
        }
    }
}
