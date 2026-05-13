namespace RetailPulse.Contracts.Routing;

/// <summary>
/// Well-known intent categories for the retail domain.
/// Each maps to a specialist agent that owns that conversation type.
/// </summary>
public static class AgentIntent
{
    public const string DemandForecasting = "demand/forecasting";
    public const string PromotionTrade = "promotion/trade";
    public const string SupplyShipments = "supply/shipments";
    public const string CompetitiveMarket = "competitive/market";
    public const string SentimentField = "sentiment/field";
    public const string MemoryManagement = "memory/management";
    public const string General = "general/fallback";

    /// <summary>All known intents for validation.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DemandForecasting,
        PromotionTrade,
        SupplyShipments,
        CompetitiveMarket,
        SentimentField,
        MemoryManagement,
        General
    ];
}

/// <summary>
/// Result of the router's intent classification for a user message.
/// </summary>
/// <param name="AgentKey">The DI key of the specialist agent to handle this message.</param>
/// <param name="Intent">The classified intent category (e.g., "demand/forecasting").</param>
/// <param name="Confidence">0.0–1.0 confidence score from the classification model.</param>
/// <param name="DetectedIntents">All intents detected (supports multi-intent messages).</param>
public record RoutingDecision(
    string AgentKey,
    string Intent,
    double Confidence,
    IReadOnlyList<string>? DetectedIntents = null
);
