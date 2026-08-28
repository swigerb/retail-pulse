using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;
using ChatResponse = RetailPulse.Contracts.ChatResponse;

namespace RetailPulse.Api.Scorecard;

/// <summary>
/// Generates portfolio scorecards by fanning out brand assessments to specialist
/// agents in parallel, scoring across weighted dimensions, and synthesizing an
/// executive brief via LLM.
/// </summary>
public class ScorecardOrchestrator
{
    private readonly IEnumerable<ISpecialistAgent> _specialists;
    private readonly IChatClient _chatClient;
    private readonly AgentDefinition _synthesisDef;
    private readonly ILogger<ScorecardOrchestrator> _logger;
    private readonly IReadOnlyList<ScorecardDimensionConfig> _scoringDimensionsConfig;

    // Specialist assessments make tool calls, so they need the same headroom the
    // consensus council was given. At 12s every dimension timed out and the whole
    // scorecard degraded to a flat neutral 5.0, which looked like a working feature
    // reporting that every brand was mediocre.
    private static readonly TimeSpan _agentTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Caps how many specialist assessments run at once across the whole scorecard.
    /// Six brands times five dimensions is thirty simultaneous model calls, which
    /// throttle each other into timeouts; six at a time completes reliably.
    /// </summary>
    private static readonly SemaphoreSlim _assessmentSlots = new(6, 6);

    /// <summary>
    /// How long a brand's score stays fresh. Long enough that navigating away and back is
    /// instant, short enough that the demo still reflects changes within a session.
    /// </summary>
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(15);

    private sealed record CachedBrandScore(BrandScore Score, DateTime ScoredAt);

    /// <summary>
    /// Process-wide so every replica request benefits, and static so it survives the
    /// scoped lifetime the orchestrator is registered with.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedBrandScore> _brandCache = new();

    /// <summary>Default dimensions — kept as a fallback so tests and legacy composition
    /// roots that don't supply a configuration list continue to work.</summary>
    internal static readonly IReadOnlyList<ScorecardDimensionConfig> DefaultScoringDimensions =
    [
        new("Demand Momentum", 0.25, "demand-forecasting"),
        new("Competitive Position", 0.20, "competitive-intel"),
        new("Supply Reliability", 0.20, "supply-chain"),
        new("Store Execution", 0.20, "store-ops"),
        new("Margin Health", 0.15, "margin-analysis"),
    ];

    public ScorecardOrchestrator(
        IEnumerable<ISpecialistAgent> specialists,
        IChatClient chatClient,
        AgentDefinition synthesisDef,
        ILogger<ScorecardOrchestrator> logger,
        IReadOnlyList<ScorecardDimensionConfig>? scoringDimensions = null)
    {
        _specialists = specialists;
        _chatClient = chatClient;
        _synthesisDef = synthesisDef;
        _logger = logger;
        _scoringDimensionsConfig = scoringDimensions is { Count: > 0 }
            ? scoringDimensions
            : DefaultScoringDimensions;
    }

    public record BrandScore(
        string Brand,
        double OverallScore,
        Dictionary<string, DimensionScore> Dimensions,
        string Summary,
        string[] ActionItems,
        long DurationMs);

    public record DimensionScore(
        string Dimension,
        double Score,
        double Weight,
        double WeightedScore,
        string Assessment,
        string AgentKey);

    public record PortfolioScorecard(
        BrandScore[] Brands,
        string ExecutiveSummary,
        string[] TopActions,
        DateTime GeneratedAt,
        long TotalDurationMs);

    /// <summary>
    /// Generate a scorecard for the specified brands. Fans out assessments in parallel.
    /// </summary>
    public async Task<PortfolioScorecard> GenerateAsync(
        string[] brands, string? region = null, bool includeSummary = true, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Generating scorecard for {Count} brands", brands.Length);

        // Fan-out: score all brands in parallel
        IEnumerable<Task<BrandScore>> brandTasks = brands.Select(b => ScoreBrandAsync(b, region, ct));
        BrandScore[] brandScores = await Task.WhenAll(brandTasks);

        // Sort by overall score descending
        BrandScore[] sorted = [.. brandScores.OrderByDescending(b => b.OverallScore)];

        // The summary is an extra model call. Callers that render per-brand cards and
        // discard it should not pay for it on every request.
        string execSummary = includeSummary
            ? await GenerateExecSummaryAsync(sorted, region, ct)
            : string.Empty;

        string[] topActions = [.. sorted
            .SelectMany(b => b.ActionItems)
            .Take(5)];

        _logger.LogInformation(
            "Scorecard complete for {Count} brands in {Ms}ms",
            brands.Length, sw.ElapsedMilliseconds);

        return new PortfolioScorecard(
            sorted, execSummary, topActions, DateTime.UtcNow, sw.ElapsedMilliseconds);
    }

    private async Task<BrandScore> ScoreBrandAsync(
        string brand, string? region, CancellationToken ct)
    {
        // Scoring one brand costs five tool-using specialist calls — measured at 7-33s
        // against the deployed backend. A brand's health does not move minute to minute,
        // so serve a recent result instead of paying that again. Without this, revisiting
        // the panel re-ran the entire portfolio (~130s) every single time.
        string cacheKey = $"{brand}|{region ?? "*"}";
        if (_brandCache.TryGetValue(cacheKey, out CachedBrandScore? cached)
            && cached is not null
            && DateTime.UtcNow - cached.ScoredAt < _cacheTtl)
        {
            _logger.LogInformation("Scorecard cache hit for {Brand}", brand);
            return cached.Score;
        }

        BrandScore score = await ScoreBrandUncachedAsync(brand, region, ct);

        // Only cache a genuinely grounded result. Caching a degraded all-neutral scorecard
        // would pin the panel to "every brand is mediocre" for the whole TTL.
        if (!IsDegraded(score))
        {
            _brandCache[cacheKey] = new CachedBrandScore(score, DateTime.UtcNow);
        }

        return score;
    }

    /// <summary>A score is degraded when no dimension produced a real assessment.</summary>
    private static bool IsDegraded(BrandScore score) =>
        score.Dimensions.Values.All(d =>
            d.Assessment.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || d.Assessment.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || d.Assessment.Contains("failed", StringComparison.OrdinalIgnoreCase));

    private async Task<BrandScore> ScoreBrandUncachedAsync(
        string brand, string? region, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dimensions = new Dictionary<string, DimensionScore>();
        var agentLookup = _specialists.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        // Evaluate each dimension in parallel
        IEnumerable<Task<DimensionScore>> dimTasks = _scoringDimensionsConfig.Select(async dim =>
        {
            if (!agentLookup.TryGetValue(dim.AgentKey, out ISpecialistAgent? agent))
            {
                return new DimensionScore(dim.Dimension, 5.0, dim.Weight, 5.0 * dim.Weight,
                    "Agent unavailable — defaulting to neutral score.", dim.AgentKey);
            }

            try
            {
                // Wait for a slot BEFORE starting the clock. A portfolio of six brands
                // fans out thirty concurrent model calls, which throttled each other
                // badly enough that most assessments burned their whole budget queueing
                // and returned a neutral 5.0. Bounding the fan-out — and only timing the
                // call once it can actually run — is what makes the scores real.
                await _assessmentSlots.WaitAsync(ct);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_agentTimeout);

                    string prompt = BuildDimensionPrompt(brand, region, dim.Dimension);
                    var request = new ChatRequest(prompt, $"scorecard-{Guid.NewGuid():N}");
                    ChatResponse response = await agent.HandleAsync(request, cts.Token);

                    return ParseDimensionScore(dim.Dimension, dim.Weight, dim.AgentKey, response.Reply);
                }
                finally
                {
                    _assessmentSlots.Release();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("{Agent} timed out scoring {Brand}/{Dim}",
                    dim.AgentKey, brand, dim.Dimension);
                return new DimensionScore(dim.Dimension, 5.0, dim.Weight, 5.0 * dim.Weight,
                    "Assessment timed out — using neutral score.", dim.AgentKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Agent} failed scoring {Brand}/{Dim}",
                    dim.AgentKey, brand, dim.Dimension);
                return new DimensionScore(dim.Dimension, 5.0, dim.Weight, 5.0 * dim.Weight,
                    "Assessment failed — using neutral score.", dim.AgentKey);
            }
        });

        DimensionScore[] dimResults = await Task.WhenAll(dimTasks);

        foreach (DimensionScore? dim in dimResults)
            dimensions[dim.Dimension] = dim;

        double overallScore = dimResults.Sum(d => d.WeightedScore);

        string summary = overallScore >= 7.5 ? $"{brand} is performing strongly across most dimensions."
            : overallScore >= 5.0 ? $"{brand} shows mixed performance — opportunities for improvement exist."
            : $"{brand} needs attention — multiple dimensions are underperforming.";

        string[] actionItems = [.. dimResults
            .Where(d => d.Score < 5.0)
            .OrderBy(d => d.Score)
            .Select(d => $"Improve {d.Dimension}: {d.Assessment}")
            .Take(3)];

        return new BrandScore(brand, Math.Round(overallScore, 1), dimensions,
            summary, actionItems, sw.ElapsedMilliseconds);
    }

    private static string BuildDimensionPrompt(string brand, string? region, string dimension)
    {
        string regionClause = string.IsNullOrWhiteSpace(region) ? "across all regions" : $"in {region}";
        return $$"""
            Rate the brand "{{brand}}" {{regionClause}} on the dimension "{{dimension}}".

            Use your tools to gather data, then respond with ONLY a JSON object:
            {
              "score": 1.0 to 10.0,
              "assessment": "One sentence explaining the score"
            }

            Score guidelines:
            - 8-10: Excellent, strong performance
            - 6-8: Good, meets expectations
            - 4-6: Mixed, some concerns
            - 2-4: Poor, needs attention
            - 1-2: Critical, immediate action required
            """;
    }

    private DimensionScore ParseDimensionScore(
        string dimension, double weight, string agentKey, string responseText)
    {
        try
        {
            int jsonStart = responseText.IndexOf('{');
            int jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                string json = responseText[jsonStart..(jsonEnd + 1)];
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                double score = root.TryGetProperty("score", out JsonElement scoreEl)
                    ? Math.Clamp(scoreEl.GetDouble(), 1.0, 10.0)
                    : 5.0;

                string assessment = root.TryGetProperty("assessment", out JsonElement assessEl)
                    ? assessEl.GetString() ?? "No assessment provided."
                    : "No assessment provided.";

                return new DimensionScore(dimension, score, weight, score * weight, assessment, agentKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse dimension score for {Dim}", dimension);
        }

        return new DimensionScore(dimension, 5.0, weight, 5.0 * weight,
            responseText[..Math.Min(200, responseText.Length)], agentKey);
    }

    private async Task<string> GenerateExecSummaryAsync(
        BrandScore[] brands, string? region, CancellationToken ct)
    {
        string brandSummaries = string.Join("\n", brands.Select(b =>
            $"- {b.Brand}: {b.OverallScore:F1}/10 — {b.Summary}"));

        string prompt = $"""
            Generate a brief executive summary (3-5 sentences) for this portfolio scorecard:

            {brandSummaries}

            Region: {region ?? "All regions"}

            Highlight the strongest brand, the weakest brand, and the single most important action.
            """;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, _synthesisDef.SystemPrompt),
                new(ChatRole.User, prompt)
            };

            var options = new ChatOptions { Temperature = (float)_synthesisDef.Temperature };
            Microsoft.Extensions.AI.ChatResponse response = await _chatClient.GetResponseAsync(messages, options, ct);
            return response.Text ?? "Portfolio scorecard generated. See brand details below.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exec summary generation failed");
            return "Portfolio scorecard generated. See brand details below.";
        }
    }
}
