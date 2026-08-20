using AgentDefinition = RetailPulse.Api.Models.AgentDefinition;

namespace RetailPulse.Tests.Guardrails.AgentDefinitions;

/// <summary>
/// Real, domain-legitimate definition fragments drawn from the shipped
/// prompts.yaml. Used to guard against over-eager pattern additions — every
/// case must be accepted by the validator under any policy combination.
/// </summary>
internal static class LegitimateCorpus
{
    public static IReadOnlyList<AgentDefinition> Definitions { get; } =
    [
        new()
        {
            Key = "demand-forecasting",
            Name = "Demand Forecast Agent",
            DisplayName = "Demand Forecasting",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are the Demand Forecasting specialist. Use historical depletion trends " +
                           "and seasonality factors to produce a 90-day forecast with confidence bounds.",
            Temperature = 0.3,
            Tools = ["GetHistoricalDemand", "GenerateForecast", "GetSeasonalityFactors", "CreateChart"],
            Intents = ["demand/forecasting"],
            KeywordFastPaths = ["demand forecast", "sell-through", "velocity forecast"],
            Role = "specialist",
            CouncilParticipant = true,
            ScorecardDimension = "Demand Momentum",
            ScorecardWeight = 0.25,
        },
        new()
        {
            Key = "promo-planning",
            Name = "Promotion Planning Agent",
            DisplayName = "Promotion Planning",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are the Promotion Planning specialist. Evaluate proposed campaigns for ROI, " +
                           "lift, and cannibalization risk; escalate high-spend proposals for approval.",
            Temperature = 0.3,
            Tools = ["GetPromoHistory", "CalculateLift", "EvaluateTiming", "EstimateROI", "RequestApproval", "CreateChart"],
            Intents = ["promotion/trade"],
            KeywordFastPaths = ["promo planning", "campaign roi"],
            Role = "specialist",
        },
        new()
        {
            Key = "competitive-intel",
            Name = "Competitive Intelligence Agent",
            DisplayName = "Competitive Intelligence",
            Model = "gpt-5.4-mini",
            SystemPrompt = "You are the Competitive Intelligence specialist. Track market share shifts, " +
                           "detect pricing pressure, and correlate competitor moves with portfolio brands.",
            Temperature = 0.4,
            Tools = ["GetCompetitorPricing", "GetMarketShare", "DetectThreats", "GetCompetitiveLandscape", "CreateChart"],
            Intents = ["competitive/market"],
            KeywordFastPaths = ["pricing pressure", "market share", "price war"],
            Role = "bespoke",
        },
        new()
        {
            Key = "memory-management",
            Name = "Memory Management Agent",
            DisplayName = "Memory Management",
            Model = "none",
            SystemPrompt = "You handle explicit memory commands — storing preferences and clearing history.",
            Temperature = 0.0,
            Tools = [],
            Intents = ["memory/management"],
            KeywordFastPaths = ["remember that", "forget"],
            Role = "bespoke",
        },
    ];
}
