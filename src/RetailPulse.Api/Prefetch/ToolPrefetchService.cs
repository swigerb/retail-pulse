using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetailPulse.Api.Caching;
using RetailPulse.Api.Tools;
using RetailPulse.Contracts.Charts;
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
    private readonly DepletionStatsTool _depletionStatsTool;
    private readonly PortfolioDepletionStatsTool _portfolioDepletionStatsTool;
    private readonly MarketShareTool _marketShareTool;
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
        DepletionStatsTool depletionStatsTool,
        PortfolioDepletionStatsTool portfolioDepletionStatsTool,
        MarketShareTool marketShareTool,
        ToolResultCache toolCache,
        ILogger<ToolPrefetchService> logger)
    {
        _historicalDemandTool = historicalDemandTool;
        _seasonalityTool = seasonalityTool;
        _depletionStatsTool = depletionStatsTool;
        _portfolioDepletionStatsTool = portfolioDepletionStatsTool;
        _marketShareTool = marketShareTool;
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
        => await PrefetchAsync(intent, entities, userMessage: null, ct);

    public async Task<IReadOnlyDictionary<string, string>> PrefetchAsync(
        string intent,
        PrefetchEntities entities,
        string? userMessage,
        CancellationToken ct = default)
    {
        ChartAcceptanceCase? curatedChart = FindCuratedChart(userMessage);
        if (curatedChart is not null)
        {
            IReadOnlyDictionary<string, string> curated = await PrefetchCuratedChartAsync(curatedChart, entities, ct);
            if (curated.Count > 0)
                return curated;
        }

        if (!ShouldPrefetch(intent, entities))
            return _emptyResults;

        var sw = Stopwatch.StartNew();
        var results = new Dictionary<string, string>();
        var tasks = new List<(string ToolName, Task<string> ResultTask)>();
        string demandPeriod = ResolveHistoricalDemandPeriod(userMessage);

        if (string.Equals(intent, AgentIntent.DemandForecasting, StringComparison.OrdinalIgnoreCase))
        {
            // Prefetch historical demand when we have at least a brand
            if (entities.Brand is not null)
            {
                var args = new Dictionary<string, object?>
                {
                    ["brand"] = entities.Brand,
                    ["region"] = entities.Region ?? "National",
                    ["channel"] = entities.Channel ?? "All",
                    ["period"] = demandPeriod
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
                        demandPeriod,
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
                    Dictionary<string, object?> cacheArgs = BuildCacheArgs(toolName, entities, demandPeriod);
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

    private async Task<IReadOnlyDictionary<string, string>> PrefetchCuratedChartAsync(
        ChartAcceptanceCase chart,
        PrefetchEntities entities,
        CancellationToken ct)
    {
        var calls = new List<(string Key, Task<string> Task)>();

        switch (chart.DataSource)
        {
            case ChartDataSource.PortfolioDepletion:
                calls.Add((
                    "GetPortfolioDepletionStats",
                    _portfolioDepletionStatsTool.GetPortfolioDepletionStats(
                        region: null,
                        period: "YTD",
                        cancellationToken: ct)));
                break;

            case ChartDataSource.DepletionStats:
                foreach (string brand in chart.RequiredEntities)
                {
                    foreach (string seededRegion in ChartAcceptanceManifest.SeededRegions)
                    {
                        calls.Add((
                            $"GetDepletionStats:{brand}:{seededRegion}",
                            _depletionStatsTool.GetDepletionStats(brand, seededRegion, "YTD", ct)));
                    }
                }
                break;

            case ChartDataSource.HistoricalDemand:
                string demandRegion = entities.Region ?? "National";
                string demandPeriod = ResolveHistoricalDemandPeriod(chart.Prompt);
                foreach (string brand in chart.RequiredEntities)
                {
                    calls.Add((
                        $"GetHistoricalDemand:{brand}",
                        _historicalDemandTool.GetHistoricalDemand(brand, demandRegion, "All", demandPeriod, ct)));
                }
                break;

            case ChartDataSource.MarketShare:
                calls.Add((
                    "GetMarketShare",
                    _marketShareTool.GetMarketShare(
                        brand: null,
                        category: entities.Category ?? "Grocery",
                        region: entities.Region ?? "National",
                        period: null,
                        cancellationToken: ct)));
                break;

            case ChartDataSource.VariantMix:
            case ChartDataSource.InventoryLevels:
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(chart), chart.DataSource, "Unknown curated chart data source.");
        }

        if (calls.Count == 0)
            return _emptyResults;

        try
        {
            await Task.WhenAll(calls.Select(c => c.Task));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "One or more curated chart prefetch calls failed — returning partial results");
        }

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, Task<string> task) in calls)
        {
            if (task.IsCompletedSuccessfully && IsUsableCuratedPayload(key, task.Result))
                results[key] = task.Result;
        }

        if (results.Count != calls.Count)
        {
            _logger.LogWarning(
                "Curated chart prefetch was incomplete or returned fallback data — using the normal tool path ({SuccessCount}/{TotalCount} usable)",
                results.Count, calls.Count);
            return _emptyResults;
        }

        _logger.LogInformation(
            "Curated chart prefetch completed — {SuccessCount}/{TotalCount} results for {ChartType}",
            results.Count, calls.Count, chart.ChartType);

        return results;
    }

    private static bool IsUsableCuratedPayload(string key, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;
            string toolName = key.Split(':', 2)[0];
            bool projectedMarketShare = toolName == "GetMarketShare" && root.ValueKind == JsonValueKind.Array;
            bool supportedRoot = projectedMarketShare || root.ValueKind == JsonValueKind.Object;
            if (!supportedRoot)
            {
                return false;
            }
            bool invalidMetadata = root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("error", out _)
                    || (root.TryGetProperty("status", out JsonElement status)
                        && status.ValueKind == JsonValueKind.String
                        && string.Equals(status.GetString(), "degraded", StringComparison.OrdinalIgnoreCase))
                    || (root.TryGetProperty("complete", out JsonElement complete)
                        && complete.ValueKind == JsonValueKind.False));

            return !invalidMetadata && (toolName switch
            {
                "GetHistoricalDemand" =>
                    root.TryGetProperty("summary", out JsonElement summary)
                    && summary.ValueKind == JsonValueKind.Object
                    && ((root.TryGetProperty("by_region", out JsonElement byRegion)
                        && byRegion.ValueKind == JsonValueKind.Array
                        && byRegion.GetArrayLength() > 0)
                        || (root.TryGetProperty("weekly_data", out JsonElement weekly)
                        && weekly.ValueKind == JsonValueKind.Array
                        && weekly.GetArrayLength() > 0)),
                "GetDepletionStats" =>
                    root.TryGetProperty("brand", out JsonElement brand)
                    && brand.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("metrics", out JsonElement metrics)
                    && metrics.ValueKind == JsonValueKind.Object,
                "GetPortfolioDepletionStats" =>
                    root.TryGetProperty("brands", out JsonElement brands)
                    && brands.ValueKind == JsonValueKind.Array
                    && brands.GetArrayLength() > 0,
                "GetMarketShare" =>
                    (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
                    || (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("share_data", out JsonElement shareData)
                        && shareData.ValueKind == JsonValueKind.Array
                        && shareData.GetArrayLength() >= 2),
                _ => false,
            });
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ChartAcceptanceCase? FindCuratedChart(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        string normalized = NormalizePrompt(message);
        return ChartAcceptanceManifest.Cases.FirstOrDefault(c =>
            string.Equals(NormalizePrompt(c.Prompt), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePrompt(string value) =>
        PromptWhitespaceRegex().Replace(value.Trim(), " ").TrimEnd('.', '!', '?');

    [GeneratedRegex(@"\s+")]
    private static partial Regex PromptWhitespaceRegex();

    private static string ResolveHistoricalDemandPeriod(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "last-12-months";

        if (message.Contains("this quarter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("current quarter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quarter to date", StringComparison.OrdinalIgnoreCase))
        {
            return "current-quarter";
        }

        if (message.Contains("this month", StringComparison.OrdinalIgnoreCase)
            || message.Contains("month to date", StringComparison.OrdinalIgnoreCase))
        {
            return "current-month";
        }

        if (message.Contains("this year", StringComparison.OrdinalIgnoreCase)
            || message.Contains("year to date", StringComparison.OrdinalIgnoreCase))
        {
            return "current-year";
        }

        Match trailingMonths = TrailingMonthsRegex().Match(message);
        return trailingMonths.Success
            ? $"last-{trailingMonths.Groups[1].Value}-months"
            : "last-12-months";
    }

    [GeneratedRegex(@"\b(?:last|past)\s+([1-9]|1\d|2[0-4])\s+months?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingMonthsRegex();

    private static Dictionary<string, object?> BuildCacheArgs(
        string toolName,
        PrefetchEntities entities,
        string demandPeriod) =>
        toolName switch
        {
            "GetHistoricalDemand" => new Dictionary<string, object?>
            {
                ["brand"] = entities.Brand,
                ["region"] = entities.Region ?? "National",
                ["channel"] = entities.Channel ?? "All",
                ["period"] = demandPeriod
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
