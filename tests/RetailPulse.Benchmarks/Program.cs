using BenchmarkDotNet.Running;
using RetailPulse.Benchmarks;

internal sealed class BenchmarkEntry
{
    public static int Main(string[] args)
    {
        // Deterministic p50/p95 baseline runner for the hybrid-execution fast path
        // (issue #95). Kept alongside BenchmarkDotNet so both live under the same
        // project entry point:
        //
        //   dotnet run -c Release --project tests/RetailPulse.Benchmarks -- baseline
        //   dotnet run -c Release --project tests/RetailPulse.Benchmarks -- baseline --out <path>
        //
        // Anything else falls through to BenchmarkDotNet as before.
        if (args.Length > 0 && string.Equals(args[0], "baseline", StringComparison.OrdinalIgnoreCase))
        {
            string[] rest = args.Length > 1 ? args[1..] : [];
            return HybridFastPathBaselineRunner.Run(rest);
        }

        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEntry).Assembly).Run(args);
        return 0;
    }
}
