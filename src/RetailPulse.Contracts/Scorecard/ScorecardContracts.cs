namespace RetailPulse.Contracts.Scorecard;

public record BrandScore(string BrandId, string BrandName, double HealthScore,
    string Trend, string TopRisk, string TopOpportunity,
    Dictionary<string, double> DimensionScores);

public record PortfolioScorecard(IReadOnlyList<BrandScore> Brands,
    double PortfolioHealth, DateTime GeneratedAt, TimeSpan GenerationTime);

public record ExplanationChain(string TraceId, string Question, string Answer,
    IReadOnlyList<ExplanationStep> Steps, double Confidence);

public record ExplanationStep(string Tool, string Input, string Output,
    string Reasoning, DateTime Timestamp);
