using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Api.Prefetch;

/// <summary>
/// Predictive tool prefetching — extracts entities from user queries and pre-fetches
/// tool data in parallel, eliminating one full LLM roundtrip for common query patterns.
/// </summary>
public partial class ToolPrefetchService
{
#pragma warning disable CS0618 // Obsolete tool proxies still used in prefetch pipeline
    private readonly HistoricalDemandTool _historicalDemandTool;
    private readonly SeasonalityFactorsTool _seasonalityTool;
#pragma warning restore CS0618
    private readonly ToolResultCache _toolCache;
    private readonly ILogger<ToolPrefetchService> _logger;

    // Brand → Category mapping for seasonality lookups
    private static readonly FrozenDictionary<string, string> _brandCategoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sierra Gold Tequila"] = "Spirits",
        ["Ridgeline Bourbon"] = "Spirits",
        ["Summit Vodka"] = "Spirits",
        ["Pacific Pale Ale"] = "Spirits",
        ["FreshMart"] = "Grocery",
        ["Harvest Table"] = "Grocery",
        ["Coastal Creamery"] = "Grocery",
        ["Mountain Trail Granola"] = "Grocery",
        ["Urban Roast Coffee"] = "Grocery",
        ["Apex Grill"] = "Quick-Serve Restaurant",
        ["Coastline Tacos"] = "Quick-Serve Restaurant",
        ["Pinnacle Hardware"] = "Home Improvement",
        ["Summit Outdoor"] = "Home Improvement",
        ["ClearDesk"] = "Office Supply",
        ["Urban Living"] = "Furniture",
        ["Foundry Home"] = "Furniture"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _knownBrands = [.. _brandCategoryMap.Keys];

    private static readonly string[] _knownRegions =
        ["Northeast", "Southeast", "Midwest", "Southwest", "West Coast", "Pacific Northwest", "National"];

    private static readonly string[] _knownChannels =
        ["On-Premise", "Off-Premise", "E-Commerce", "Grocery", "Convenience", "All"];

    private static readonly string[] _knownCategories =
        ["Spirits", "Grocery", "Quick-Serve Restaurant", "Home Improvement", "Office Supply", "Furniture"];

    [GeneratedRegex(@"\b(On-Premise|Off-Premise|E-Commerce)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

#pragma warning disable CS0618 // Obsolete tool proxies still used in prefetch pipeline
    public ToolPrefetchService(
        HistoricalDemandTool historicalDemandTool,
        SeasonalityFactorsTool seasonalityTool,
        ToolResultCache toolCache,
        ILogger<ToolPrefetchService> logger)
    {
        _historicalDemandTool = historicalDemandTool;
        _seasonalityTool = seasonalityTool;
        _toolCache = toolCache;
        _logger = logger;
    }
#pragma warning restore CS0618

    /// <summary>
    /// Extracts brand, region, channel, and category entities from a user message
    /// using fast pattern matching (no LLM call).
    /// </summary>
    public PrefetchEntities ExtractEntities(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return PrefetchEntities.Empty;

        string? brand = null;
        string? region = null;
        string? channel = null;
        string? category = null;

        // Brand extraction — longest match first to avoid partial matches
        foreach (string? knownBrand in _knownBrands.OrderByDescending(b => b.Length))
        {
            if (message.Contains(knownBrand, StringComparison.OrdinalIgnoreCase))
            {
                brand = knownBrand;
                break;
            }
        }

        // Region extraction
        foreach (string knownRegion in _knownRegions)
        {
            if (message.Contains(knownRegion, StringComparison.OrdinalIgnoreCase))
            {
                region = knownRegion;
                break;
            }
        }

        // Channel extraction
        Match channelMatch = ChannelPattern().Match(message);
        if (channelMatch.Success)
        {
            channel = _knownChannels.FirstOrDefault(c =>
                string.Equals(c, channelMatch.Value, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            foreach (string knownChannel in _knownChannels)
            {
                if (knownChannel != "All" && message.Contains(knownChannel, StringComparison.OrdinalIgnoreCase))
                {
                    channel = knownChannel;
                    break;
                }
            }
        }

        // Category — derive from brand if detected, otherwise check explicit mentions
        if (brand is not null && _brandCategoryMap.TryGetValue(brand, out string? derivedCategory))
        {
            category = derivedCategory;
        }
        else
        {
            foreach (string knownCategory in _knownCategories)
            {
                if (message.Contains(knownCategory, StringComparison.OrdinalIgnoreCase))
                {
                    category = knownCategory;
                    break;
                }
            }
        }

        return new PrefetchEntities(brand, region, channel, category);
    }

    /// <summary>
    /// Pre-fetches tool data for the given intent and extracted entities.
    /// Returns a dictionary of toolName → JSON result, or empty if nothing to prefetch.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> PrefetchAsync(
        string intent,
        PrefetchEntities entities,
        CancellationToken ct = default)
    {
        if (!ShouldPrefetch(intent, entities))
            return _emptyResults;

        var sw = Stopwatch.StartNew();
        var results = new Dictionary<string, string>();
        var tasks = new List<(string ToolName, Task<string> ResultTask)>();

        if (string.Equals(intent, AgentIntent.DemandForecasting, StringComparison.OrdinalIgnoreCase))
        {
            // Prefetch historical demand when we have at least a brand
            if (entities.Brand is not null)
            {
                var args = new Dictionary<string, object?>
                {
                    ["brand"] = entities.Brand,
                    ["region"] = entities.Region ?? "National",
                    ["channel"] = entities.Channel ?? "All"
                };

                string? cached = _toolCache.TryGet("GetHistoricalDemand", args);
                if (cached is not null)
                {
                    results["GetHistoricalDemand"] = cached;
                }
                else
                {
                    tasks.Add(("GetHistoricalDemand", _historicalDemandTool.GetHistoricalDemand(
                        entities.Brand,
                        entities.Region ?? "National",
                        entities.Channel ?? "All",
                        ct)));
                }
            }

            // Prefetch seasonality when we have a category
            if (entities.Category is not null)
            {
                var args = new Dictionary<string, object?>
                {
                    ["category"] = entities.Category
                };

                string? cached = _toolCache.TryGet("GetSeasonalityFactors", args);
                if (cached is not null)
                {
                    results["GetSeasonalityFactors"] = cached;
                }
                else
                {
                    tasks.Add(("GetSeasonalityFactors", _seasonalityTool.GetSeasonalityFactors(
                        entities.Category,
                        ct)));
                }
            }
        }

        if (tasks.Count == 0 && results.Count == 0)
            return _emptyResults;

        // Execute all prefetch calls in parallel
        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks.Select(t => t.ResultTask));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "One or more prefetch tasks failed — returning partial results");
            }

            foreach ((string? toolName, Task<string>? resultTask) in tasks)
            {
                if (resultTask.IsCompletedSuccessfully)
                {
                    results[toolName] = resultTask.Result;

                    // Store in tool cache for subsequent LLM tool calls
                    Dictionary<string, object?> cacheArgs = BuildCacheArgs(toolName, entities);
                    _toolCache.Set(toolName, cacheArgs, resultTask.Result);
                }
                else if (resultTask.IsFaulted)
                {
                    _logger.LogWarning(resultTask.Exception?.InnerException,
                        "Prefetch for {Tool} failed — tool will be called by LLM fallback", toolName);
                }
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Prefetch completed in {ElapsedMs}ms — {SuccessCount}/{TotalCount} tools succeeded for intent={Intent}",
            sw.ElapsedMilliseconds, results.Count, results.Count + tasks.Count(t => t.ResultTask.IsFaulted), intent);

        return results;
    }

    private static Dictionary<string, object?> BuildCacheArgs(string toolName, PrefetchEntities entities) =>
        toolName switch
        {
            "GetHistoricalDemand" => new Dictionary<string, object?>
            {
                ["brand"] = entities.Brand,
                ["region"] = entities.Region ?? "National",
                ["channel"] = entities.Channel ?? "All"
            },
            "GetSeasonalityFactors" => new Dictionary<string, object?>
            {
                ["category"] = entities.Category
            },
            _ => []
        };

    private static bool ShouldPrefetch(string intent, PrefetchEntities entities) =>
        string.Equals(intent, AgentIntent.DemandForecasting, StringComparison.OrdinalIgnoreCase)
        && (entities.Brand is not null || entities.Category is not null);

    private static readonly IReadOnlyDictionary<string, string> _emptyResults =
        new Dictionary<string, string>().AsReadOnly();
}

/// <summary>
/// Entities extracted from a user query for predictive tool prefetching.
/// </summary>
public record PrefetchEntities(string? Brand, string? Region, string? Channel, string? Category)
{
    public static readonly PrefetchEntities Empty = new(null, null, null, null);
    public bool HasAny => Brand is not null || Region is not null || Channel is not null || Category is not null;
}
