using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Charts;
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
    private readonly PortfolioDepletionStatsTool? _portfolioDepletionTool;
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
        ILogger<ToolPrefetchService> logger,
        PortfolioDepletionStatsTool? portfolioDepletionTool = null)
    {
        _historicalDemandTool = historicalDemandTool;
        _seasonalityTool = seasonalityTool;
        _toolCache = toolCache;
        _logger = logger;
        _portfolioDepletionTool = portfolioDepletionTool;
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
    public Task<IReadOnlyDictionary<string, string>> PrefetchAsync(
        string intent,
        PrefetchEntities entities,
        CancellationToken ct = default)
        => PrefetchAsync(intent, entities, chartIntent: null, ct);

    /// <summary>
    /// Chart-intent aware overload. When the router has classified the request as
    /// an explicit chart request AND the shape is one the tool-selection layer is
    /// known to flake on, this overload deterministically pre-calls the correct
    /// AGGREGATE tool so the payload is in model context regardless of which tool
    /// the LLM happens to pick on a given run.
    ///
    /// Two shapes are covered — both keyed on <see cref="ChartIntent.ChartType"/>
    /// and the routed specialist intent, NEVER on prompt-text matching:
    ///
    ///   * <b>Category-scoped table</b> (chartType=<c>table</c> + a category cue
    ///     extracted from the message): pre-calls
    ///     <c>GetPortfolioDepletionStats(region="AllRegions", category=...)</c>
    ///     so the model sees per-brand per-region depletion stats for every
    ///     brand in the tenant category in one call — closes the acceptance gap
    ///     where prompt #25 fetched only one Home Improvement brand.
    ///
    ///   * <b>National share pie/donut</b> (chartType=<c>pie</c>/<c>donut</c> +
    ///     <see cref="AgentIntent.CompetitiveMarket"/> + no specific region):
    ///     pre-calls <c>GetPortfolioDepletionStats(region="National")</c>
    ///     which the prompts guide already treats as the source of truth for
    ///     national market share (see <c>prompts.yaml</c>: "prefer this over
    ///     GetMarketShare when the user asks for a national or portfolio-wide
    ///     pie/donut share breakdown"). Closes the acceptance gap where prompt
    ///     #21 emitted no chart on one of two runs due to tool-selection non-
    ///     determinism.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> PrefetchAsync(
        string intent,
        PrefetchEntities entities,
        ChartIntent? chartIntent,
        CancellationToken ct = default)
    {
        // Chart-aware aggregate prefetches take precedence: they answer whole
        // acceptance prompts in one deterministic call. When neither condition
        // fires we still run the entity-driven demand prefetch below.
        Dictionary<string, string>? aggregateResults = null;
        if (chartIntent is { IsExplicitChartRequest: true } ci
            && _portfolioDepletionTool is not null)
        {
            aggregateResults = await TryPrefetchChartAggregates(intent, entities, ci, ct);
        }

        IReadOnlyDictionary<string, string> demandResults = await PrefetchDemandAsync(intent, entities, ct);

        if (aggregateResults is null || aggregateResults.Count == 0)
        {
            return demandResults;
        }

        // Merge — aggregate results win on key collisions since they are the
        // deterministic authoritative payload for the chart shape.
        var merged = new Dictionary<string, string>(demandResults, StringComparer.OrdinalIgnoreCase);
        foreach ((string k, string v) in aggregateResults)
        {
            merged[k] = v;
        }
        return merged;
    }

    private async Task<Dictionary<string, string>?> TryPrefetchChartAggregates(
        string intent,
        PrefetchEntities entities,
        ChartIntent chartIntent,
        CancellationToken ct)
    {
        string chartType = chartIntent.ChartType ?? string.Empty;
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Category-scoped table intent → per-brand per-region depletion stats
        // for every brand in the tenant category, in one call.
        if (chartType.Equals("table", StringComparison.OrdinalIgnoreCase)
            && entities.Category is not null)
        {
            var args = new Dictionary<string, object?>
            {
                ["region"] = "AllRegions",
                ["period"] = "YTD",
                ["category"] = entities.Category,
            };
            string? cached = _toolCache.TryGet("GetPortfolioDepletionStats", args);
            if (cached is not null)
            {
                results["GetPortfolioDepletionStats"] = cached;
            }
            else
            {
                try
                {
                    string json = await _portfolioDepletionTool!.GetPortfolioDepletionStats(
                        region: "AllRegions",
                        period: "YTD",
                        category: entities.Category,
                        brands: null,
                        cancellationToken: ct);
                    results["GetPortfolioDepletionStats"] = json;
                    _toolCache.Set("GetPortfolioDepletionStats", args, json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chart-aggregate prefetch (category table) failed for category={Category}", entities.Category);
                }
            }
        }

        // National share pie/donut intent → national portfolio depletion aggregate
        // (the prompts guide treats this as the source of truth for national share).
        bool nationalShareChart =
            (chartType.Equals("pie", StringComparison.OrdinalIgnoreCase)
             || chartType.Equals("donut", StringComparison.OrdinalIgnoreCase))
            && string.Equals(intent, AgentIntent.CompetitiveMarket, StringComparison.OrdinalIgnoreCase)
            && (entities.Region is null
                || entities.Region.Equals("National", StringComparison.OrdinalIgnoreCase));
        if (nationalShareChart)
        {
            var args = new Dictionary<string, object?>
            {
                ["region"] = "National",
                ["period"] = "YTD",
                ["category"] = entities.Category,
            };
            string? cached = _toolCache.TryGet("GetPortfolioDepletionStats", args);
            if (cached is not null)
            {
                results["GetPortfolioDepletionStats"] = cached;
            }
            else
            {
                try
                {
                    string json = await _portfolioDepletionTool!.GetPortfolioDepletionStats(
                        region: "National",
                        period: "YTD",
                        category: entities.Category,
                        brands: null,
                        cancellationToken: ct);
                    results["GetPortfolioDepletionStats"] = json;
                    _toolCache.Set("GetPortfolioDepletionStats", args, json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chart-aggregate prefetch (national share) failed");
                }
            }
        }

        return results.Count == 0 ? null : results;
    }

    private async Task<IReadOnlyDictionary<string, string>> PrefetchDemandAsync(
        string intent,
        PrefetchEntities entities,
        CancellationToken ct)
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
