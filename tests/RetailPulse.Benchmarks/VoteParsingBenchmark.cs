using System.Reflection;
using BenchmarkDotNet.Attributes;
using RetailPulse.Api.Consensus;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Benchmarks the JSON vote parsing in ConsensusOrchestrator.ParseVote
/// to measure serialization hot-path performance.
/// </summary>
[MemoryDiagnoser]
public class VoteParsingBenchmark
{
    private static readonly MethodInfo ParseVoteMethod =
        typeof(ConsensusOrchestrator).GetMethod("ParseVote", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private ConsensusOrchestrator _orchestrator = null!;
    private string _validVoteJson = null!;
    private string _voteWithSurroundingText = null!;
    private string _malformedJson = null!;
    private string _minimalJson = null!;
    private TimeSpan _elapsed;

    [GlobalSetup]
    public void Setup()
    {
        // Create an uninitialized instance to invoke the private ParseVote method.
        // ParseVote only uses _logger which defaults to null — safe for benchmarking.
        _orchestrator = (ConsensusOrchestrator)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ConsensusOrchestrator));

        _elapsed = TimeSpan.FromMilliseconds(150);

        _validVoteJson = """
            {
              "rating": "Green",
              "reasoning": "Strong sell-through rates across all SKUs. Inventory turns are healthy at 4.2x. No stockout risk detected in the next 14-day window.",
              "confidence": 0.92,
              "key_metrics": ["sell-through: 87%", "inventory turns: 4.2x", "stockout risk: low"]
            }
            """;

        _voteWithSurroundingText = """
            Based on my analysis, here is my assessment:
            
            {
              "rating": "Yellow",
              "reasoning": "Mixed signals in competitive positioning. Market share stable but pricing pressure increasing from private label alternatives.",
              "confidence": 0.78,
              "key_metrics": ["market share: 23.1%", "price gap: -8%", "promo lift: 1.4x"]
            }
            
            Please note this is based on the latest available data.
            """;

        _malformedJson = "This is not valid JSON at all, just plain text about the brand being Red flagged.";

        _minimalJson = """{"rating":"Red","reasoning":"Critical stockout.","confidence":0.99,"key_metrics":[]}""";
    }

    [Benchmark(Description = "Parse: valid structured JSON vote")]
    public object? ParseValidVote()
        => ParseVoteMethod.Invoke(_orchestrator, ["demand-forecasting", "Demand Forecasting", _validVoteJson, _elapsed]);

    [Benchmark(Description = "Parse: JSON embedded in surrounding text")]
    public object? ParseVoteWithSurroundingText()
        => ParseVoteMethod.Invoke(_orchestrator, ["competitive-intel", "Competitive Intel", _voteWithSurroundingText, _elapsed]);

    [Benchmark(Description = "Parse: malformed (heuristic fallback)")]
    public object? ParseMalformedVote()
        => ParseVoteMethod.Invoke(_orchestrator, ["supply-chain", "Supply Chain", _malformedJson, _elapsed]);

    [Benchmark(Description = "Parse: minimal compact JSON")]
    public object? ParseMinimalVote()
        => ParseVoteMethod.Invoke(_orchestrator, ["demand-forecasting", "Demand Forecasting", _minimalJson, _elapsed]);
}
