using FluentAssertions;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Proves the deterministic gate is stable against an unchanged codebase before it is ever
/// enabled as a blocker. Runs the harness N times in-process, serializes each report with a
/// fixed timestamp, and asserts every serialized payload is byte-identical. Because the
/// entire pipeline is pure regex + string compare on the router's keyword layer (no live
/// LLM, no randomness, no wall-clock reads in the scoring path), the reports MUST match.
///
/// If this suite ever begins to flake, the harness has crossed the line from deterministic
/// into model-dependent and the gate should not be re-enabled until the drift is fixed.
/// </summary>
public sealed class StabilityTests
{
    private static readonly DateTimeOffset _fixedNow =
        DateTimeOffset.Parse("2026-08-20T15:53:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void RepeatedRuns_ProduceByteIdenticalReports(int iterations)
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        var runner = new EvaluationRunner();

        string first = EvaluationRunner.SerializeReport(runner.RunOffline(dataset, _fixedNow));
        for (int i = 1; i < iterations; i++)
        {
            string next = EvaluationRunner.SerializeReport(runner.RunOffline(dataset, _fixedNow));
            next.Should().Be(first,
                $"iteration {i + 1} of {iterations} produced a different report — determinism regression");
        }
    }

    [Fact]
    public void RepeatedRuns_KeepGateGreen()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        var runner = new EvaluationRunner();

        for (int i = 0; i < 5; i++)
        {
            EvaluationReport report = runner.RunOffline(dataset, _fixedNow);
            report.Summary.GateStatus.Should().Be("pass",
                $"iteration {i + 1}: unchanged-codebase gate must remain green");
            report.Summary.DeterministicFail.Should().Be(0,
                $"iteration {i + 1}: no case may transiently fail on stable code");
        }
    }
}
