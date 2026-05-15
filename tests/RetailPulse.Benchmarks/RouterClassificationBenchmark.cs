using System.Reflection;
using BenchmarkDotNet.Attributes;
using RetailPulse.Api.Agents.Routing;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Benchmarks the keyword fast-path matching in RetailOpsRouter.TryKeywordClassify
/// without invoking any LLM calls.
/// </summary>
[MemoryDiagnoser]
public class RouterClassificationBenchmark
{
    private static readonly MethodInfo TryKeywordClassifyMethod =
        typeof(RetailOpsRouter).GetMethod("TryKeywordClassify", BindingFlags.NonPublic | BindingFlags.Static)!;

    private string _demandForecastMessage = null!;
    private string _supplyChainMessage = null!;
    private string _portfolioHealthRegex = null!;
    private string _noMatchMessage = null!;
    private string _longMessage = null!;

    [GlobalSetup]
    public void Setup()
    {
        _demandForecastMessage = "What is the demand forecast for Q4?";
        _supplyChainMessage = "Show me the current shipment status for warehouse 7";
        _portfolioHealthRegex = "How is the Oreo brand performing this quarter?";
        _noMatchMessage = "Tell me a joke about retail";
        _longMessage = string.Concat(Enumerable.Repeat("Some filler text about general topics. ", 50))
                       + "Check the supply chain status.";
    }

    [Benchmark(Description = "Keyword match: demand forecast")]
    public object? MatchDemandForecast()
        => TryKeywordClassifyMethod.Invoke(null, [_demandForecastMessage]);

    [Benchmark(Description = "Keyword match: supply chain")]
    public object? MatchSupplyChain()
        => TryKeywordClassifyMethod.Invoke(null, [_supplyChainMessage]);

    [Benchmark(Description = "Regex match: portfolio health")]
    public object? MatchPortfolioHealthRegex()
        => TryKeywordClassifyMethod.Invoke(null, [_portfolioHealthRegex]);

    [Benchmark(Description = "No match: falls through all patterns")]
    public object? NoMatch()
        => TryKeywordClassifyMethod.Invoke(null, [_noMatchMessage]);

    [Benchmark(Description = "Long message with keyword at end")]
    public object? LongMessageMatch()
        => TryKeywordClassifyMethod.Invoke(null, [_longMessage]);
}
