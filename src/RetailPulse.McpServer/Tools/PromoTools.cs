using System.ComponentModel;
using ModelContextProtocol.Server;
using RetailPulse.McpServer.Data;

namespace RetailPulse.McpServer.Tools;

[McpServerToolType]
public static class PromoTools
{
    [McpServerTool(Name = "GetPromoHistory")]
    [Description("Get historical promotion campaign data with outcomes. Returns campaign name, dates, spend, lift%, ROI, and success rating. Filterable by brand, region, promo type, and number of months.")]
    public static object GetPromoHistory(
        RetailPulseDb data,
        [Description("Brand name to filter (e.g. 'Sierra Gold Tequila', 'FreshMart'). Omit for all brands.")] string? brand = null,
        [Description("Region to filter (e.g. 'Northeast', 'West Coast'). Omit for all regions.")] string? region = null,
        [Description("Promo type to filter ('discount', 'bogo', 'display', 'digital', 'bundle'). Omit for all types.")] string? promoType = null,
        [Description("Number of months of history to return (1-24). Default: 18")] int months = 18) => data.GetPromoHistory(brand, region, promoType, months);

    [McpServerTool(Name = "CalculateLift")]
    [Description("Estimate expected volume uplift for a promotion based on brand, region, promo type, and spend. Uses historical lift coefficients with diminishing returns for spend beyond optimal levels. Returns expected lift percent, confidence level, and count of similar historical campaigns.")]
    public static object CalculateLift(
        RetailPulseDb data,
        [Description("Brand name (required, e.g. 'Ridgeline Bourbon')")] string brand,
        [Description("Region (required, e.g. 'Southwest')")] string region,
        [Description("Promotion type (required: 'discount', 'bogo', 'display', 'digital', 'bundle')")] string promoType,
        [Description("Planned spend in dollars (required, e.g. 150000)")] double spend)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(promoType))
            return new { error = "Parameter 'promoType' is required." };
        return spend <= 0 ? (new { error = "Parameter 'spend' must be greater than 0." }) : data.CalculateLift(brand, region, promoType, spend);
    }

    [McpServerTool(Name = "EvaluateTiming")]
    [Description("Evaluate the timing of a proposed promotion. Checks for overlapping campaigns, seasonality fit, and cannibalization risk from recent similar promos. Returns timing score (0-1), conflicts, seasonality boost, and risk factors.")]
    public static object EvaluateTiming(
        RetailPulseDb data,
        [Description("Brand name (required)")] string brand,
        [Description("Region (required)")] string region,
        [Description("Proposed start date (required, ISO format e.g. '2026-06-01')")] string startDate,
        [Description("Proposed end date (required, ISO format e.g. '2026-06-28')")] string endDate)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (!DateOnly.TryParse(startDate, out DateOnly start))
            return new { error = "Parameter 'startDate' must be a valid date (e.g. '2026-06-01')." };
        if (!DateOnly.TryParse(endDate, out DateOnly end))
            return new { error = "Parameter 'endDate' must be a valid date (e.g. '2026-06-28')." };
        return end <= start ? (new { error = "endDate must be after startDate." }) : data.EvaluateTiming(brand, region, start, end);
    }

    [McpServerTool(Name = "EstimateROI")]
    [Description("Full ROI estimation for a proposed promotion combining lift analysis, timing evaluation, and spend effectiveness. Returns expected ROI with upper/lower bounds, confidence level, and breakeven point.")]
    public static object EstimateROI(
        RetailPulseDb data,
        [Description("Brand name (required)")] string brand,
        [Description("Region (required)")] string region,
        [Description("Promotion type (required: 'discount', 'bogo', 'display', 'digital', 'bundle')")] string promoType,
        [Description("Planned spend in dollars (required)")] double spend,
        [Description("Duration in weeks (required, 1-12)")] int durationWeeks)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };
        if (string.IsNullOrWhiteSpace(promoType))
            return new { error = "Parameter 'promoType' is required." };
        if (spend <= 0)
            return new { error = "Parameter 'spend' must be greater than 0." };
        return durationWeeks is < 1 or > 12
            ? (new { error = "Parameter 'durationWeeks' must be between 1 and 12." })
            : data.EstimateROI(brand, region, promoType, spend, durationWeeks);
    }
}
