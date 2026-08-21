using System.Reflection;
using BenchmarkDotNet.Attributes;
using RetailPulse.Api.Agents.Routing;

namespace RetailPulse.Benchmarks;

/// <summary>
/// Measures the decision-layer fast-path overhead for the reference single-specialist
/// prompt used by issue #95 ("hybrid execution: fast single-shot path vs plan path").
///
/// This exercises <c>RetailOpsRouter.TryKeywordClassify</c> only — a pure, in-process
/// call with no network, LLM, or MCP dependency — so it produces a deterministic
/// pre-change baseline against which the post-change hybrid decision layer can be
/// compared without model or transport variance.
///
/// A concise, machine-readable p50/p95 baseline artifact is produced by the
/// companion runner in <see cref="HybridFastPathBaselineRunner"/>; this
/// BenchmarkDotNet class exists so the same code path also lives under the
/// project's standard benchmark suite for future BDN-based comparison.
/// </summary>
[MemoryDiagnoser]
public class HybridFastPathBenchmark
{
    private static readonly MethodInfo TryKeywordClassifyMethod =
        typeof(RetailOpsRouter).GetMethod("TryKeywordClassify", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private RetailOpsRouter _router = null!;
    private string _referencePrompt = null!;

    [GlobalSetup]
    public void Setup()
    {
        _referencePrompt = HybridFastPathBaselineRunner.ReferencePrompt;
        _router = HybridFastPathBaselineRunner.CreateRouterUninitialized();
    }

    [Benchmark(Description = "Fast-path: 'How is Sierra Gold Tequila performing in the Northeast?'")]
    public object? SingleSpecialistFastPath()
        => TryKeywordClassifyMethod.Invoke(_router, [_referencePrompt]);
}
