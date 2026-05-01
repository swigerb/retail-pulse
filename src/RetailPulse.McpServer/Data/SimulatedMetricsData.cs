using System.Globalization;
using System.Text;
using RetailPulse.Contracts;

namespace RetailPulse.McpServer.Data;

public class SimulatedMetricsData
{
    private readonly TenantConfiguration _tenant;
    private readonly Dictionary<(string Brand, string Region), DepletionRecord> _depletionData;
    private readonly Dictionary<(string Brand, string Region), ShipmentRecord> _shipmentData;
    private readonly Dictionary<(string Brand, string Region), SentimentRecord> _sentimentData;

    public SimulatedMetricsData(ITenantProvider tenantProvider)
    {
        _tenant = tenantProvider.GetTenant();
        _depletionData = GenerateDepletionData();
        _shipmentData = GenerateShipmentData();
        _sentimentData = GenerateSentimentData();
    }

    public object GetDepletionStats(string brand, string region, string period)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        // "National" aggregates all regions into a single rollup
        if (region.Trim().Equals("National", StringComparison.OrdinalIgnoreCase) ||
            region.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return GetNationalDepletionStats(brand.Trim(), period);
        }

        var key = (brand.Trim(), region.Trim());

        if (!_depletionData.TryGetValue(key, out var record))
        {
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var match = _depletionData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(region.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                record = _depletionData[match];
            }
            else
                return new { error = $"No data found for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        var periodMultiplier = period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85,
            "Q2" => 1.05,
            "Q3" => 0.95,
            "Q4" => 1.15,
            _ => 1.0
        };

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            period = period ?? "YTD",
            metrics = new
            {
                depletions_yoy = AdjustPercentage(record.DepletionsYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(record.SellThroughYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(record.InventoryWeeks * (2.0 - periodMultiplier), 1),
                status = record.Status
            },
            sentiment_summary = record.SentimentSummary
        };
    }

    public object GetShipmentStats(string brand, string region, string period)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        var key = (brand.Trim(), region.Trim());

        if (!_shipmentData.TryGetValue(key, out var record))
        {
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var normalizedRegion = region.Trim();
            var match = _shipmentData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(normalizedRegion, StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                record = _shipmentData[match];
            }
            else
                return new { error = $"No shipment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        var periodMultiplier = period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85,
            "Q2" => 1.05,
            "Q3" => 0.95,
            "Q4" => 1.15,
            _ => 1.0
        };

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            period = period ?? "YTD",
            shipments = new
            {
                shipments_yoy = AdjustPercentage(record.ShipmentsYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(record.SellThroughYoY, periodMultiplier),
                depletions_yoy = AdjustPercentage(record.DepletionsYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(record.InventoryWeeks * (2.0 - periodMultiplier), 1),
                cases_shipped = (int)(record.CasesShipped * periodMultiplier),
                cases_depleted = (int)(record.CasesDepleted * periodMultiplier)
            },
            anomaly = new
            {
                type = record.AnomalyType,
                risk_level = record.RiskLevel,
                is_pipeline_clog = record.AnomalyType == "pipeline_clog",
                shipment_depletion_gap = record.CasesShipped - record.CasesDepleted
            },
            analysis = record.Analysis
        };
    }

    public object GetFieldSentiment(string brand, string region)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        var key = (brand.Trim(), region.Trim());

        if (!_sentimentData.TryGetValue(key, out var sentimentRecord))
        {
            var normalizedBrand = NormalizeDiacritics(brand.Trim());
            var match = _sentimentData.Keys.FirstOrDefault(k =>
                NormalizeDiacritics(k.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase) &&
                k.Region.Contains(region.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != default)
            {
                key = match;
                sentimentRecord = _sentimentData[match];
            }
            else
                return new { error = $"No sentiment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
        }

        return new
        {
            brand = key.Item1,
            region = key.Item2,
            source = "Field Sales & Distributor Reports",
            reporting_period = "Current YTD",
            sentiment = sentimentRecord.Sentiment
        };
    }

    // ── Data Generation ──────────────────────────────────────────────────

    private Dictionary<(string Brand, string Region), DepletionRecord> GenerateDepletionData()
    {
        var data = new Dictionary<(string Brand, string Region), DepletionRecord>(StringTupleComparer.Instance);

        var statuses = new[] { "On Track", "Growth Leader", "Declining", "Overstocked" };

        foreach (var brand in _tenant.Brands)
        {
            // Each brand gets a base growth profile influenced by its category and price segment
            var brandSeed = GetStableHash(brand.Name);
            var baseTrend = GetBaseTrend(brand, brandSeed);

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var regionVariance = (regionRng.NextDouble() - 0.5) * 8.0; // ±4%
                var depletionGrowth = Math.Round(baseTrend + regionVariance, 1);
                var sellThroughGrowth = Math.Round(depletionGrowth + (regionRng.NextDouble() - 0.5) * 4.0, 1);

                // Inventory weeks inversely correlated with growth
                var inventoryWeeks = Math.Round(Math.Max(2.5, 7.0 - depletionGrowth * 0.3 + regionRng.NextDouble() * 3.0), 1);

                // Status based on metrics
                var status = DetermineDepletionStatus(depletionGrowth, sellThroughGrowth, inventoryWeeks);

                var summary = GenerateDepletionSummary(brand, region, depletionGrowth, sellThroughGrowth, inventoryWeeks, status, regionRng);

                data[(brand.Name, region)] = new DepletionRecord(
                    FormatPercentage(depletionGrowth),
                    FormatPercentage(sellThroughGrowth),
                    inventoryWeeks,
                    status,
                    summary);
            }
        }

        return data;
    }

    private Dictionary<(string Brand, string Region), ShipmentRecord> GenerateShipmentData()
    {
        var data = new Dictionary<(string Brand, string Region), ShipmentRecord>(StringTupleComparer.Instance);

        foreach (var brand in _tenant.Brands)
        {
            var baseTrend = GetBaseTrend(brand, GetStableHash(brand.Name));

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"ship|{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var shipmentGrowth = Math.Round(baseTrend + (regionRng.NextDouble() - 0.3) * 6.0, 1);
                var sellThroughGrowth = Math.Round(baseTrend + (regionRng.NextDouble() - 0.5) * 5.0, 1);
                var depletionGrowth = Math.Round(sellThroughGrowth + (regionRng.NextDouble() - 0.5) * 3.0, 1);

                var inventoryWeeks = Math.Round(Math.Max(2.5, 6.5 - depletionGrowth * 0.25 + regionRng.NextDouble() * 3.0), 1);

                // Base cases scaled by brand category and price
                var baseCases = brand.PriceSegment switch
                {
                    "Ultra-Premium" => 2_000 + regionRng.Next(1_000, 5_000),
                    "Premium" => 5_000 + regionRng.Next(3_000, 15_000),
                    _ => 8_000 + regionRng.Next(5_000, 25_000)
                };
                var casesShipped = baseCases + (int)(baseCases * shipmentGrowth / 100.0);
                var casesDepleted = baseCases + (int)(baseCases * depletionGrowth / 100.0);

                var (anomalyType, riskLevel) = DetermineAnomalyType(shipmentGrowth, sellThroughGrowth, depletionGrowth, inventoryWeeks);

                var analysis = GenerateShipmentAnalysis(brand, region, shipmentGrowth, sellThroughGrowth, depletionGrowth, inventoryWeeks, casesShipped, casesDepleted, anomalyType);

                data[(brand.Name, region)] = new ShipmentRecord(
                    FormatPercentage(shipmentGrowth),
                    FormatPercentage(sellThroughGrowth),
                    FormatPercentage(depletionGrowth),
                    inventoryWeeks,
                    casesShipped,
                    casesDepleted,
                    anomalyType,
                    riskLevel,
                    analysis);
            }
        }

        return data;
    }

    private Dictionary<(string Brand, string Region), SentimentRecord> GenerateSentimentData()
    {
        var data = new Dictionary<(string Brand, string Region), SentimentRecord>(StringTupleComparer.Instance);

        foreach (var brand in _tenant.Brands)
        {
            var baseTrend = GetBaseTrend(brand, GetStableHash(brand.Name));

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"sent|{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var sentiment = GenerateFieldSentiment(brand, region, baseTrend, regionRng);
                data[(brand.Name, region)] = new SentimentRecord(sentiment);
            }
        }

        return data;
    }

    // ── Trend & Status Helpers ───────────────────────────────────────────

    private static double GetBaseTrend(BrandConfig brand, int seed)
    {
        var rng = new Random(seed);
        // Categories have different momentum
        var categoryBoost = brand.Category switch
        {
            "Tequila" => 4.0 + rng.NextDouble() * 4.0,
            "Mezcal" => 5.0 + rng.NextDouble() * 5.0,
            "Bourbon" => 3.0 + rng.NextDouble() * 5.0,
            "Ready-to-Drink" => 2.0 + rng.NextDouble() * 6.0,
            "Gin" => 1.0 + rng.NextDouble() * 3.0,
            "Rum" => -2.0 + rng.NextDouble() * 4.0,
            "Vodka" => -3.0 + rng.NextDouble() * 3.0,
            _ => -1.0 + rng.NextDouble() * 4.0
        };
        // Ultra-Premium tends to outperform within category
        var segmentBoost = brand.PriceSegment switch
        {
            "Ultra-Premium" => 2.0,
            "Premium" => 0.5,
            _ => -1.0
        };
        return Math.Round(categoryBoost + segmentBoost, 1);
    }

    private static string DetermineDepletionStatus(double depletionGrowth, double sellThroughGrowth, double inventoryWeeks)
    {
        if (inventoryWeeks > 8.5 && sellThroughGrowth < 0) return "Overstocked";
        if (depletionGrowth > 6.0 && sellThroughGrowth > 4.0) return "Growth Leader";
        if (depletionGrowth < -1.0 || sellThroughGrowth < -2.0) return "Declining";
        return "On Track";
    }

    private static (string AnomalyType, string RiskLevel) DetermineAnomalyType(
        double shipmentGrowth, double sellThroughGrowth, double depletionGrowth, double inventoryWeeks)
    {
        var gap = shipmentGrowth - sellThroughGrowth;

        if (gap > 6.0 && inventoryWeeks > 8.0)
            return ("pipeline_clog", inventoryWeeks > 10.0 ? "critical" : "high");

        if (shipmentGrowth < -4.0 && depletionGrowth > 4.0)
            return ("supply_constraint", depletionGrowth > 8.0 ? "critical" : "high");

        if (shipmentGrowth > 8.0 && sellThroughGrowth > 5.0 && inventoryWeeks < 5.0)
            return ("growth_opportunity", "low");

        if (gap > 3.0 && inventoryWeeks > 5.0)
            return ("pipeline_building", "medium");

        if (depletionGrowth < 0 && shipmentGrowth < 0 && Math.Abs(gap) < 2.0)
            return ("declining_aligned", inventoryWeeks > 7.0 ? "medium" : "low");

        return ("healthy", "low");
    }

    // ── Narrative Generation ─────────────────────────────────────────────

    private string GenerateDepletionSummary(BrandConfig brand, string region,
        double depletionGrowth, double sellThroughGrowth, double inventoryWeeks, string status, Random rng)
    {
        var variants = brand.Variants.Count > 0 ? brand.Variants[rng.Next(brand.Variants.Count)] : "core";

        return status switch
        {
            "Growth Leader" => $"{brand.Name} showing strong momentum in {region} with {FormatPercentage(depletionGrowth)} depletion growth. " +
                $"The {variants} variant is driving trial in on-premise accounts. Distributor velocity up across key retail chains. " +
                $"Weeks on hand at {inventoryWeeks} indicates healthy pipeline with strong consumer pull.",

            "Overstocked" => $"Distributor warehouses in {region} holding excess {brand.Name} inventory at {inventoryWeeks} weeks on hand. " +
                $"Sell-through at {FormatPercentage(sellThroughGrowth)} suggests consumer demand softening. " +
                $"Competitive pressure in the {brand.Category.ToLower()} segment is creating headwinds. Promotional support may be needed to clear pipeline.",

            "Declining" => $"{brand.Name} facing headwinds in {region} with depletions at {FormatPercentage(depletionGrowth)}. " +
                $"Category dynamics shifting as consumers explore alternatives in the {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} tier. " +
                $"On-premise placements under pressure. Field team recommends targeted activation programs.",

            _ => $"{brand.Name} performing steadily in {region} with depletions at {FormatPercentage(depletionGrowth)}. " +
                $"The {variants} expression maintaining velocity in both on- and off-premise channels. " +
                $"Inventory at {inventoryWeeks} weeks — well within target range. Distribution holding stable."
        };
    }

    private string GenerateShipmentAnalysis(BrandConfig brand, string region,
        double shipmentGrowth, double sellThroughGrowth, double depletionGrowth,
        double inventoryWeeks, int casesShipped, int casesDepleted,
        string anomalyType)
    {
        var gap = casesShipped - casesDepleted;
        var model = _tenant.Distribution?.Model ?? "Three-Tier";

        return anomalyType switch
        {
            "pipeline_clog" => $"Shipments to distributors in {region} are up {FormatPercentage(shipmentGrowth)}, but consumer sell-through is at {FormatPercentage(sellThroughGrowth)}. " +
                $"Distributor warehouses holding {casesShipped:N0} cases shipped vs {casesDepleted:N0} depleted — a {gap:N0} case gap. " +
                $"At {inventoryWeeks} weeks on hand, the {model} pipeline is building pressure. " +
                $"Recommend reducing shipments and deploying depletion programs to clear excess inventory.",

            "supply_constraint" => $"Cannot ship enough {brand.Name} to meet {region} demand. Shipments at {FormatPercentage(shipmentGrowth)} while " +
                $"consumer demand grows {FormatPercentage(depletionGrowth)}. Key accounts requesting more allocation. " +
                $"Lost some placements to competitors who can supply. Strategic allocation decisions needed to protect highest-value accounts.",

            "growth_opportunity" => $"Shipping aggressively to fill new {brand.Name} distribution in {region} — but sell-through is keeping pace. " +
                $"Distributors requesting more allocation. Retail chains expanding shelf space. " +
                $"Healthy growth pipeline at {inventoryWeeks} weeks on hand. Every new distribution point converting at above-average velocity.",

            "pipeline_building" => $"Shipping ahead of sell-through as {brand.Name} distribution expands in {region}. " +
                $"{casesShipped:N0} cases shipped vs {casesDepleted:N0} depleted. Expected pipeline fill during distribution expansion — " +
                $"monitoring to ensure new points of distribution convert. Weeks on hand at {inventoryWeeks} within acceptable range.",

            "declining_aligned" => $"Shipments and sell-through for {brand.Name} both declining in {region} at similar rates — pipeline is stable but the floor is dropping. " +
                $"The {brand.Category.ToLower()} segment faces structural headwinds. Volume management is appropriate but category strategy review needed.",

            _ => $"Shipments and sell-through aligned for {brand.Name} in {region}. {casesShipped:N0} cases shipped, {casesDepleted:N0} depleted. " +
                $"No pipeline concerns — the market is genuinely consuming what's being shipped. Weeks on hand at {inventoryWeeks} is healthy."
        };
    }

    private string GenerateFieldSentiment(BrandConfig brand, string region, double baseTrend, Random rng)
    {
        var variantList = string.Join(", ", brand.Variants.Take(3));
        var channel = _tenant.Channels.Count > 0 ? _tenant.Channels[rng.Next(_tenant.Channels.Count)] : "retail";

        // Build a rich, multi-sentence narrative
        var sentences = new List<string>();

        if (baseTrend > 5.0)
        {
            sentences.Add($"{brand.Name} showing exceptional momentum in {region}. Distributor reps report strong pull across {channel} accounts.");
            sentences.Add($"The {brand.Category.ToLower()} category is a major tailwind — consumers actively seeking out {brand.PriceSegment.ToLower()} options.");
            sentences.Add($"Key variants ({variantList}) all performing above plan. Retail velocity up significantly vs prior year.");
            sentences.Add($"Field team recommends expanding distribution and securing additional shelf placements before competitors react.");
        }
        else if (baseTrend > 2.0)
        {
            sentences.Add($"Steady performance for {brand.Name} in {region} with positive distributor sentiment.");
            sentences.Add($"{channel} accounts maintaining consistent reorders. The {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} segment is competitive but {brand.Name} holding its position.");
            sentences.Add($"Consumer engagement with {variantList} expressions is stable. Some competitive pressure from new entrants in the category.");
            sentences.Add($"Distributors note the brand benefits from established consumer loyalty and reliable supply chain execution.");
        }
        else if (baseTrend > -1.0)
        {
            sentences.Add($"{brand.Name} holding flat in {region} with mixed signals from the field.");
            sentences.Add($"Some {channel} accounts reporting steady velocity while others see softening. The {brand.Category.ToLower()} category overall is under moderate pressure.");
            sentences.Add($"Price sensitivity increasing among consumers at the {brand.PriceSegment.ToLower()} tier.");
            sentences.Add($"Field team recommends targeted promotional support and menu placement programs to defend share.");
        }
        else
        {
            sentences.Add($"{brand.Name} facing headwinds in {region}. Distributor sentiment cautious.");
            sentences.Add($"The {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} segment is losing occasions to other spirit categories. {channel} placements under pressure.");
            sentences.Add($"Competitors gaining shelf space. Consumers migrating to trending categories.");
            sentences.Add($"Need strategic review of the brand's position in {region}. Promotional spend showing diminishing returns — may need positioning refresh.");
        }

        return string.Join(" ", sentences);
    }

    private object GetNationalDepletionStats(string brand, string? period)
    {
        var normalizedBrand = NormalizeDiacritics(brand);
        var regionalData = _depletionData
            .Where(kv => NormalizeDiacritics(kv.Key.Brand).Contains(normalizedBrand, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (regionalData.Count == 0)
            return new { error = $"No data found for brand '{brand}'.", available_brands = GetAvailableBrands() };

        var periodMultiplier = period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85, "Q2" => 1.05, "Q3" => 0.95, "Q4" => 1.15, _ => 1.0
        };

        var avgDepletions = regionalData.Average(kv => ParsePercentage(kv.Value.DepletionsYoY));
        var avgSellThrough = regionalData.Average(kv => ParsePercentage(kv.Value.SellThroughYoY));
        var avgInventory = regionalData.Average(kv => kv.Value.InventoryWeeks);

        return new
        {
            brand = regionalData.First().Key.Brand,
            region = "National",
            period = period ?? "YTD",
            regions_aggregated = regionalData.Count,
            metrics = new
            {
                depletions_yoy = FormatPercentage(Math.Round(avgDepletions * periodMultiplier, 1)),
                sell_through_yoy = FormatPercentage(Math.Round(avgSellThrough * periodMultiplier, 1)),
                inventory_weeks_on_hand = Math.Round(avgInventory * (2.0 - periodMultiplier), 1),
                status = avgDepletions > 0 ? "Growing" : avgDepletions > -2 ? "Stable" : "Declining"
            },
            regional_breakdown = regionalData.Select(kv => new
            {
                region = kv.Key.Region,
                depletions_yoy = AdjustPercentage(kv.Value.DepletionsYoY, periodMultiplier),
                status = kv.Value.Status
            }).ToArray()
        };
    }

    private static double ParsePercentage(string pct) =>
        double.TryParse(pct.TrimEnd('%').TrimStart('+'), out var val) ? val : 0;

    // ── Utility Methods ──────────────────────────────────────────────────

    private string[] GetAvailableBrands() =>
        _tenant.Brands.Select(b => b.Name).ToArray();

    private string[] GetAvailableRegions() =>
        [.. _tenant.Regions, "National"];

    private static string FormatPercentage(double value) =>
        (value >= 0 ? "+" : "") + value.ToString("F1") + "%";

    private static string AdjustPercentage(string pct, double multiplier)
    {
        if (double.TryParse(pct.TrimEnd('%').TrimStart('+'), out var val))
        {
            var adjusted = Math.Round(val * multiplier, 1);
            return (adjusted >= 0 ? "+" : "") + adjusted + "%";
        }
        return pct;
    }

    private static int GetStableHash(string input)
    {
        // Simple deterministic hash for seeding
        unchecked
        {
            int hash = 17;
            foreach (var c in input)
                hash = hash * 31 + c;
            return Math.Abs(hash);
        }
    }

    private static string NormalizeDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

// Record types shared across the data layer
public record DepletionRecord(
    string DepletionsYoY,
    string SellThroughYoY,
    double InventoryWeeks,
    string Status,
    string SentimentSummary);

public record ShipmentRecord(
    string ShipmentsYoY,
    string SellThroughYoY,
    string DepletionsYoY,
    double InventoryWeeks,
    int CasesShipped,
    int CasesDepleted,
    string AnomalyType,
    string RiskLevel,
    string Analysis);

public record SentimentRecord(string Sentiment);

internal sealed class StringTupleComparer : IEqualityComparer<(string Brand, string Region)>
{
    public static readonly StringTupleComparer Instance = new();
    public bool Equals((string Brand, string Region) x, (string Brand, string Region) y) =>
        string.Equals(x.Brand, y.Brand, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Region, y.Region, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode((string Brand, string Region) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Brand),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Region));
}
