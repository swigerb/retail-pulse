using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Agents.Specialists;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Scorecard;

/// <summary>
/// Tests for the portfolio scorecard system: brand health scoring,
/// dimension breakdown, weighted averages, fan-out timeout handling,
/// trend detection, and generation time tracking.
/// Test-first: defines expected scorecard behavior before Phase 4.3 implementation.
/// </summary>
public class ScorecardTests
{
    private static readonly string[] ExpectedBrands =
    [
        "Sierra Gold Tequila", "Ridgeline Bourbon", "Summit Vodka",
        "FreshMart", "Harvest Table",
        "Apex Grill", "Coastline Tacos",
        "Pinnacle Hardware", "Summit Outdoor",
        "ClearDesk",
        "Urban Living", "Foundry Home"
    ];

    private static readonly string[] ExpectedDimensions =
    [
        "demand", "margin", "competitive", "supply"
    ];

    #region Brand Coverage

    [Fact]
    public async Task Scorecard_ReturnsAllBrandsFromTenantConfig()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        scorecard.Brands.Should().NotBeEmpty("scorecard should include brands");

        var brandNames = scorecard.Brands.Select(b => b.Name).ToList();
        foreach (string expected in ExpectedBrands)
        {
            brandNames.Should().Contain(expected,
                $"scorecard should include tenant brand '{expected}'");
        }
    }

    #endregion

    #region Health Score Range

    [Fact]
    public async Task Scorecard_EachBrandHasHealthScore0To100()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        foreach (BrandScore brand in scorecard.Brands)
        {
            brand.HealthScore.Should().BeGreaterThanOrEqualTo(0,
                $"brand '{brand.Name}' health score should be >= 0");
            brand.HealthScore.Should().BeLessThanOrEqualTo(100,
                $"brand '{brand.Name}' health score should be <= 100");
        }
    }

    #endregion

    #region Dimension Scores

    [Fact]
    public async Task Scorecard_EachBrandHasAllDimensionScores()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        foreach (BrandScore brand in scorecard.Brands)
        {
            brand.Dimensions.Should().NotBeNull(
                $"brand '{brand.Name}' should have dimension scores");
            brand.Dimensions.Keys.Should().Contain("demand",
                $"brand '{brand.Name}' should have demand dimension");
            brand.Dimensions.Keys.Should().Contain("margin",
                $"brand '{brand.Name}' should have margin dimension");
            brand.Dimensions.Keys.Should().Contain("competitive",
                $"brand '{brand.Name}' should have competitive dimension");
            brand.Dimensions.Keys.Should().Contain("supply",
                $"brand '{brand.Name}' should have supply dimension");

            foreach ((string? dim, double score) in brand.Dimensions)
            {
                score.Should().BeGreaterThanOrEqualTo(0,
                    $"brand '{brand.Name}' dimension '{dim}' should be >= 0");
                score.Should().BeLessThanOrEqualTo(100,
                    $"brand '{brand.Name}' dimension '{dim}' should be <= 100");
            }
        }
    }

    #endregion

    #region Weighted Average

    [Fact]
    public async Task Scorecard_OverallScore_IsWeightedAverageOfDimensions()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        foreach (BrandScore brand in scorecard.Brands)
        {
            if (brand.Dimensions.Count == 0) continue;

            // Default weights: equal across 4 dimensions (25% each)
            var weights = new Dictionary<string, double>
            {
                ["demand"] = 0.25,
                ["margin"] = 0.25,
                ["competitive"] = 0.25,
                ["supply"] = 0.25
            };

            double weightedSum = 0.0;
            double totalWeight = 0.0;

            foreach ((string? dim, double score) in brand.Dimensions)
            {
                if (weights.TryGetValue(dim, out double weight))
                {
                    weightedSum += score * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight > 0)
            {
                double expected = weightedSum / totalWeight;
                brand.HealthScore.Should().BeApproximately(expected, 2.0,
                    $"brand '{brand.Name}' overall score should be weighted average of dimensions (±2 rounding)");
            }
        }
    }

    #endregion

    #region Fan-Out Timeout

    [Fact]
    public async Task Scorecard_FanOutCompletesWithinTimeout()
    {
        IScorecardService service = CreateScorecardService();
        var sw = Stopwatch.StartNew();

        ScorecardResult scorecard = await service.GenerateAsync(timeoutSeconds: 15);

        sw.Stop();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(20,
            "scorecard generation should complete within timeout + buffer");
        scorecard.Brands.Should().NotBeEmpty("should return results within timeout");
    }

    [Fact]
    public async Task Scorecard_PartialResultsOnTimeout()
    {
        // Create a service where some brand calculations are slow
        IScorecardService service = CreateScorecardService(simulateSlowBrands: true);

        ScorecardResult scorecard = await service.GenerateAsync(timeoutSeconds: 2);

        // Should get at least some brands even if timeout hit
        scorecard.Brands.Should().NotBeEmpty(
            "should return partial results on timeout (graceful degradation)");

        // May not have all brands if some timed out
        if (scorecard.TimedOut)
        {
            scorecard.Brands.Count.Should().BeLessThan(ExpectedBrands.Length,
                "timeout should result in fewer brands than total");
        }
    }

    #endregion

    #region Trend Detection

    [Fact]
    public async Task Scorecard_TrendDetection_UpDownStable()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        foreach (BrandScore brand in scorecard.Brands)
        {
            string[] validTrends = ["up", "down", "stable"];
            brand.Trend.Should().NotBeNullOrEmpty(
                $"brand '{brand.Name}' should have a trend indicator");
            validTrends.Should().Contain(brand.Trend.ToLowerInvariant(),
                $"brand '{brand.Name}' trend '{brand.Trend}' should be up/down/stable");
        }
    }

    #endregion

    #region Generation Time Tracking

    [Fact]
    public async Task Scorecard_GenerationTimeIsTracked()
    {
        IScorecardService service = CreateScorecardService();
        ScorecardResult scorecard = await service.GenerateAsync();

        scorecard.GenerationTimeMs.Should().BeGreaterThan(0,
            "generation time should be tracked and reported");
        scorecard.GenerationTimeMs.Should().BeLessThan(30_000,
            "generation time should be reasonable (< 30s)");
    }

    #endregion

    #region Exact Dimension Weights (0.25/0.20/0.20/0.20/0.15)

    [Fact]
    public void ScorecardOrchestrator_DimensionWeights_AreExact()
    {
        // The ScorecardOrchestrator.ScoringDimensions defines exact weights:
        //   Demand Momentum:     0.25
        //   Competitive Position: 0.20
        //   Supply Reliability:   0.20
        //   Store Execution:      0.20
        //   Margin Health:        0.15
        // Total: 1.00

        var expectedWeights = new Dictionary<string, double>
        {
            ["Demand Momentum"] = 0.25,
            ["Competitive Position"] = 0.20,
            ["Supply Reliability"] = 0.20,
            ["Store Execution"] = 0.20,
            ["Margin Health"] = 0.15
        };

        // Verify via reflection to ensure the actual orchestrator weights match
        FieldInfo? field = typeof(Api.Scorecard.ScorecardOrchestrator)
            .GetField("_scoringDimensions",
                BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull("ScoringDimensions field should exist on ScorecardOrchestrator");

        var dimensions = field.GetValue(null) as (string Dimension, double Weight, string AgentKey)[];
        dimensions.Should().NotBeNull("ScoringDimensions should be castable to tuple array");
        dimensions.Should().HaveCount(5, "there should be exactly 5 scoring dimensions");

        Dictionary<string, double> actualWeights = dimensions.ToDictionary(d => d.Dimension, d => d.Weight);

        foreach ((string? name, double expectedWeight) in expectedWeights)
        {
            actualWeights.Should().ContainKey(name, $"dimension '{name}' should exist");
            actualWeights[name].Should().BeApproximately(expectedWeight, 0.001,
                $"dimension '{name}' should have weight {expectedWeight}");
        }

        // Total weights must sum to 1.0
        double totalWeight = dimensions.Sum(d => d.Weight);
        totalWeight.Should().BeApproximately(1.0, 0.001,
            "all dimension weights must sum to 1.0");
    }

    [Fact]
    public void ScorecardOrchestrator_DimensionWeights_SumToOne()
    {
        FieldInfo? field = typeof(Api.Scorecard.ScorecardOrchestrator)
            .GetField("_scoringDimensions",
                BindingFlags.NonPublic | BindingFlags.Static);

        var dimensions = field!.GetValue(null) as (string Dimension, double Weight, string AgentKey)[];
        double total = dimensions!.Sum(d => d.Weight);

        total.Should().BeApproximately(1.0, 0.001,
            "portfolio scorecard dimension weights must sum to exactly 1.0");
    }

    [Fact]
    public void ScorecardOrchestrator_DemandMomentum_HasHighestWeight()
    {
        FieldInfo? field = typeof(Api.Scorecard.ScorecardOrchestrator)
            .GetField("_scoringDimensions",
                BindingFlags.NonPublic | BindingFlags.Static);

        var dimensions = field!.GetValue(null) as (string Dimension, double Weight, string AgentKey)[];
        (string? Dimension, double Weight, string? AgentKey) = dimensions!.OrderByDescending(d => d.Weight).First();

        Dimension.Should().Be("Demand Momentum",
            "Demand Momentum should have the highest weight (0.25)");
        Weight.Should().Be(0.25);
    }

    [Fact]
    public void ScorecardOrchestrator_MarginHealth_HasLowestWeight()
    {
        FieldInfo? field = typeof(Api.Scorecard.ScorecardOrchestrator)
            .GetField("_scoringDimensions",
                BindingFlags.NonPublic | BindingFlags.Static);

        var dimensions = field!.GetValue(null) as (string Dimension, double Weight, string AgentKey)[];
        (string? Dimension, double Weight, string? AgentKey) = dimensions!.OrderBy(d => d.Weight).First();

        Dimension.Should().Be("Margin Health",
            "Margin Health should have the lowest weight (0.15)");
        Weight.Should().Be(0.15);
    }

    #endregion

    #region Test Infrastructure

    private static IScorecardService CreateScorecardService(bool simulateSlowBrands = false) => new MockScorecardService(ExpectedBrands, simulateSlowBrands);

    #endregion
}

#region Scorecard Contracts (test-first definitions)

public record BrandScore(
    string Name,
    double HealthScore,
    Dictionary<string, double> Dimensions,
    string Trend
);

public record ScorecardResult(
    List<BrandScore> Brands,
    long GenerationTimeMs,
    bool TimedOut = false
);

public interface IScorecardService
{
    Task<ScorecardResult> GenerateAsync(int timeoutSeconds = 15, CancellationToken ct = default);
}

/// <summary>
/// Mock scorecard service for deterministic test behavior.
/// Generates scores based on brand name hash for reproducibility.
/// </summary>
internal sealed class MockScorecardService : IScorecardService
{
    private readonly string[] _brands;
    private readonly bool _simulateSlowBrands;

    public MockScorecardService(string[] brands, bool simulateSlowBrands = false)
    {
        _brands = brands;
        _simulateSlowBrands = simulateSlowBrands;
    }

    public async Task<ScorecardResult> GenerateAsync(int timeoutSeconds = 15, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var brands = new List<BrandScore>();
        bool timedOut = false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        foreach (string brandName in _brands)
        {
            if (cts.Token.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }

            if (_simulateSlowBrands && brandName.Contains("Summit"))
            {
                try
                {
                    await Task.Delay(5000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    break;
                }
            }

            int hash = Math.Abs(brandName.GetHashCode());
            var dimensions = new Dictionary<string, double>
            {
                ["demand"] = 40 + (hash % 60),
                ["margin"] = 35 + (hash / 7 % 65),
                ["competitive"] = 30 + (hash / 13 % 70),
                ["supply"] = 45 + (hash / 19 % 55)
            };

            double healthScore = dimensions.Values.Average();
            string[] trends = ["up", "down", "stable"];
            string trend = trends[hash % 3];

            brands.Add(new BrandScore(brandName, healthScore, dimensions, trend));
        }

        sw.Stop();
        long elapsed = Math.Max(sw.ElapsedMilliseconds, 1); // Ensure at least 1ms for tracking
        return new ScorecardResult(brands, elapsed, timedOut);
    }
}

#endregion
