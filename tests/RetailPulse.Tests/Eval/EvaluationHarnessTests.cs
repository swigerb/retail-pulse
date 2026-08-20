using FluentAssertions;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// The CI gate. Runs the offline deterministic evaluation harness against the versioned
/// golden dataset and asserts:
/// <list type="bullet">
/// <item>the harness produces a well-formed report</item>
/// <item>every deterministically-graded property on every deterministic case passes</item>
/// <item>run cost stays under the documented per-run cap</item>
/// </list>
/// A hard failure here means either the golden expectations drifted from live router behavior
/// or the router's deterministic classification regressed. Both are equally worth catching.
/// </summary>
public sealed class EvaluationHarnessTests
{
    [Fact]
    public void OfflineDeterministicGate_PassesAllCases()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        EvaluationReport report = new EvaluationRunner().RunOffline(
            dataset,
            nowUtc: DateTimeOffset.Parse("2026-08-20T15:53:00Z", System.Globalization.CultureInfo.InvariantCulture));

        // Persist a report artifact next to the test binary so devs/CI can retrieve it.
        string artifactDir = Path.Combine(AppContext.BaseDirectory, "EvalArtifacts");
        Directory.CreateDirectory(artifactDir);
        File.WriteAllText(
            Path.Combine(artifactDir, "eval-report-offline.json"),
            EvaluationRunner.SerializeReport(report));

        // The gate itself: every deterministically-graded case must pass, and cost must
        // stay under the documented cap.
        report.Summary.GateStatus.Should().Be("pass",
            $"deterministic gate must hold — {report.Summary.DeterministicFail} case(s) failed on this run");
        report.Summary.DeterministicPassRate.Should().BeGreaterThanOrEqualTo(
            EvaluationRunner.CiGateThreshold,
            $"CI gate threshold is {EvaluationRunner.CiGateThreshold:P0}");
        report.Summary.DeterministicFail.Should().Be(0,
            "any deterministic failure is a real regression — the golden encodes actual router behavior");
        report.Cost.UsdEstimated.Should().BeLessThanOrEqualTo(
            EvaluationRunner.CostCapUsd,
            $"offline runs must stay well below the documented ${EvaluationRunner.CostCapUsd:F2} per-run cap");

        // Structure invariants: report must round-trip cleanly.
        string json = EvaluationRunner.SerializeReport(report);
        EvaluationReport roundTripped = EvaluationRunner.DeserializeReport(json);
        roundTripped.Cases.Should().HaveCount(dataset.Cases.Count);
    }

    [Fact]
    public void EveryCase_Reports_ExplicitChart_And_ChartType_Consistently()
    {
        // Structural invariant: if explicit_chart is false, chart_type must be null in both
        // observed and expected. This catches golden inconsistencies at run time.
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        EvaluationReport report = new EvaluationRunner().RunOffline(dataset);

        foreach (CaseResult c in report.Cases)
        {
            if (!c.ExplicitChart.Observed)
            {
                c.ChartType.Observed.Should().BeNull(
                    $"case {c.Id}: no chart request must yield no chart type");
            }

            if (!c.ExplicitChart.Expected)
            {
                c.ChartType.Expected.Should().BeNull(
                    $"case {c.Id}: golden expectation is inconsistent (explicit_chart=false but chart_type set)");
            }
        }
    }
}
