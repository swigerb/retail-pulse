using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Orchestrates a full offline deterministic evaluation over a <see cref="GoldenDataset"/>
/// and returns a self-contained <see cref="EvaluationReport"/>. The offline mode does not
/// consult any live model; token/cost figures reflect prompt-side estimates only and are
/// used to prove the harness lives well under its bounded per-run cost cap.
/// </summary>
public sealed class EvaluationRunner
{
    /// <summary>Harness version — bump when scoring semantics change.</summary>
    public const string HarnessVersion = "1.0.0";

    /// <summary>Bounded per-run USD cost cap. Offline runs stay at $0.00.</summary>
    public const double CostCapUsd = 5.00;

    /// <summary>Documented CI gate threshold (fraction of deterministically-graded cases that must pass).</summary>
    public const double CiGateThreshold = 1.00;

    private static readonly JsonSerializerOptions _reportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Run every case in <paramref name="dataset"/> through the deterministic scorer.</summary>
    public EvaluationReport RunOffline(GoldenDataset dataset, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var evaluator = new DeterministicEvaluator();
        var results = new List<CaseResult>(dataset.Cases.Count);
        foreach (GoldenCase c in dataset.Cases)
        {
            results.Add(evaluator.Evaluate(c));
        }

        return EvaluationReport.From(
            dataset,
            results,
            harnessVersion: HarnessVersion,
            costCapUsd: CostCapUsd,
            ciGateThreshold: CiGateThreshold,
            nowUtc: nowUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Serialize a report to a stable, sorted, indented JSON string.</summary>
    public static string SerializeReport(EvaluationReport report) =>
        JsonSerializer.Serialize(report, _reportJsonOptions);

    /// <summary>Deserialize a report from JSON (used by baseline diffing).</summary>
    public static EvaluationReport DeserializeReport(string json)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<EvaluationReport>(json, opts)
            ?? throw new InvalidOperationException("Report JSON deserialized to null.");
    }
}
