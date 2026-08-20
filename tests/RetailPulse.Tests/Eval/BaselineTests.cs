using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Regression diff against the versioned baseline. The baseline is a full report snapshot
/// captured at a specific commit for the current platform; it lets later waves see the
/// effect of a change as a real, per-case diff instead of a subjective impression.
///
/// This test compares the fresh run's per-case scored properties (observed values only)
/// against the baseline. If a case now scores differently — either because the router
/// behavior changed or because the golden expectations changed — the test prints a
/// per-property diff and fails.
///
/// To intentionally roll the baseline forward:
/// 1. Run the harness (any Eval test triggers it; the report is written to the test
///    binary's <c>EvalArtifacts/eval-report-offline.json</c>).
/// 2. Copy that file over <c>tests/RetailPulse.Tests/Eval/Data/baseline-v1.json</c>.
/// 3. Commit both the change that shifted behavior and the refreshed baseline in the
///    same commit, so <c>git blame</c> tells the story.
/// </summary>
public sealed class BaselineTests
{
    private static readonly DateTimeOffset _fixedNow =
        DateTimeOffset.Parse("2026-08-20T15:53:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void CurrentRun_MatchesVersionedBaseline_PerCase()
    {
        GoldenDataset dataset = GoldenDatasetLoader.Load();
        EvaluationReport current = new EvaluationRunner().RunOffline(dataset, _fixedNow);

        string baselineJson = File.ReadAllText(GoldenDatasetLoader.BaselinePath());
        EvaluationReport baseline = EvaluationRunner.DeserializeReport(baselineJson);

        // Case-count parity is worth catching separately — a dataset shrink shouldn't quietly
        // regress coverage.
        current.Cases.Should().HaveCount(baseline.Cases.Count,
            "adding or removing golden cases must be accompanied by a baseline refresh");

        var baselineById = baseline.Cases.ToDictionary(c => c.Id);
        var diffs = new StringBuilder();

        foreach (CaseResult run in current.Cases)
        {
            if (!baselineById.TryGetValue(run.Id, out CaseResult? baseCase))
            {
                diffs.AppendLine(CultureInfo.InvariantCulture,
                    $"case '{run.Id}': absent from baseline (new case) — refresh baseline in the same commit");
                continue;
            }

            AppendIfChanged(diffs, run.Id, "explicit_chart",
                baseCase.ExplicitChart.Observed, run.ExplicitChart.Observed);
            AppendIfChanged(diffs, run.Id, "chart_type",
                baseCase.ChartType.Observed, run.ChartType.Observed);
            AppendIfChanged(diffs, run.Id, "routing_intent",
                baseCase.RoutingIntent.Observed, run.RoutingIntent.Observed);
            AppendIfChanged(diffs, run.Id, "llm_call_made",
                baseCase.LlmCallMade.Observed, run.LlmCallMade.Observed);
            AppendIfChanged(diffs, run.Id, "memory_command",
                baseCase.MemoryCommand.Observed, run.MemoryCommand.Observed);
        }

        diffs.Length.Should().Be(0,
            "current run must match versioned baseline. Diffs:\n" + diffs);
    }

    [Fact]
    public void Baseline_File_IsWellFormed_AndConsistent()
    {
        string json = File.ReadAllText(GoldenDatasetLoader.BaselinePath());
        json.Should().NotBeNullOrWhiteSpace();

        EvaluationReport baseline = EvaluationRunner.DeserializeReport(json);

        baseline.Run.Mode.Should().Be("offline-deterministic",
            "the shipped baseline is captured from the offline deterministic harness — "
            + "live captures should be stored in a separately-named file");
        baseline.Summary.GateStatus.Should().Be("pass",
            "a shipped baseline should represent a passing snapshot");
        baseline.Cost.UsdEstimated.Should().BeLessThanOrEqualTo(EvaluationRunner.CostCapUsd,
            "baseline captures must respect the same cost cap the runtime gate does");

        // The baseline must serialize back to the same string it came from (byte-for-byte
        // stable format). This prevents contributors from introducing subtle formatting
        // drift (e.g. hand edits with different indent) that would silently invalidate
        // future diffs.
        string reserialized = EvaluationRunner.SerializeReport(baseline);
        NormalizeLineEndings(reserialized).Should().Be(
            NormalizeLineEndings(json),
            "baseline file must be a byte-for-byte deterministic serialization; "
            + "regenerate it from the harness rather than editing by hand");
    }

    private static void AppendIfChanged<T>(StringBuilder diffs, string id, string property, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            diffs.AppendLine(CultureInfo.InvariantCulture,
                $"case '{id}' property '{property}' baseline={FormatValue(before)} run={FormatValue(after)}");
        }
    }

    private static string FormatValue<T>(T value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => JsonSerializer.Serialize(value),
    };

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n").TrimEnd();
}
