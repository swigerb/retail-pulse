using System.Text.Json.Serialization;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Structured output of one harness run. Serialized to the report JSON and the versioned
/// baseline. All aggregates are computed at construction time so the report is a value-type
/// snapshot suitable for direct comparison.
/// </summary>
public sealed record EvaluationReport
{
    [JsonPropertyName("run")] public RunEnvelope Run { get; init; } = new();
    [JsonPropertyName("cost")] public CostEnvelope Cost { get; init; } = new();
    [JsonPropertyName("summary")] public SummaryEnvelope Summary { get; init; } = new();
    [JsonPropertyName("category_pass_rate")]
    public IReadOnlyDictionary<string, double?> CategoryPassRate { get; init; }
        = new Dictionary<string, double?>();
    [JsonPropertyName("cases")] public IReadOnlyList<CaseResult> Cases { get; init; } = [];
    [JsonPropertyName("model_rubric")] public ModelRubricEnvelope ModelRubric { get; init; } = new();

    internal static EvaluationReport From(
        GoldenDataset dataset,
        IReadOnlyList<CaseResult> cases,
        string harnessVersion,
        double costCapUsd,
        double ciGateThreshold,
        DateTimeOffset nowUtc)
    {
        int deterministicCases = cases.Count(c => c.DeterministicallyGraded);
        int llmRequiredCases = cases.Count - deterministicCases;
        int deterministicPass = cases.Count(c => c.DeterministicallyGraded && c.AllPropertiesPassed);
        int deterministicFail = deterministicCases - deterministicPass;

        double passRate = deterministicCases == 0
            ? 1.0
            : (double)deterministicPass / deterministicCases;

        var categoryRates = cases
            .GroupBy(c => c.Category)
            .ToDictionary(
                g => g.Key,
                double? (g) =>
                {
                    List<CaseResult> det = [.. g.Where(x => x.DeterministicallyGraded)];
                    return det.Count == 0
                        ? null
                        : (double)det.Count(x => x.AllPropertiesPassed) / det.Count;
                });

        int totalPromptTokens = cases.Sum(c => c.EstimatedPromptTokens);

        return new EvaluationReport
        {
            Run = new RunEnvelope
            {
                TimestampUtc = nowUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Mode = "offline-deterministic",
                HarnessVersion = harnessVersion,
                DatasetVersion = dataset.Version,
                TotalCases = cases.Count,
                DeterministicCases = deterministicCases,
                LlmRequiredCases = llmRequiredCases,
                CiGateThreshold = ciGateThreshold,
            },
            Cost = new CostEnvelope
            {
                PromptTokens = totalPromptTokens,
                CompletionTokens = 0,
                UsdEstimated = 0.0,
                CapUsd = costCapUsd,
                Notes = "Offline deterministic harness — no live model calls. Prompt tokens are the "
                    + "sum-over-cases of Prompt.Length/4 (rough estimator) and are informational.",
            },
            Summary = new SummaryEnvelope
            {
                DeterministicPass = deterministicPass,
                DeterministicFail = deterministicFail,
                DeterministicPassRate = passRate,
                GateStatus = passRate >= ciGateThreshold ? "pass" : "fail",
            },
            CategoryPassRate = new SortedDictionary<string, double?>(categoryRates),
            Cases = cases,
            ModelRubric = new ModelRubricEnvelope
            {
                Status = "not-run",
                Note = "Model-graded rubric (refusal quality, clarification quality, answer fluency) "
                    + "is intentionally separate. The deterministic gate never depends on it. When a "
                    + "future live evaluation pass is wired in, its scores land here and are reported "
                    + "alongside — but do not gate — the deterministic pass rate.",
            },
        };
    }
}

public sealed record RunEnvelope
{
    [JsonPropertyName("timestamp_utc")] public string TimestampUtc { get; init; } = "";
    [JsonPropertyName("mode")] public string Mode { get; init; } = "";
    [JsonPropertyName("harness_version")] public string HarnessVersion { get; init; } = "";
    [JsonPropertyName("dataset_version")] public int DatasetVersion { get; init; }
    [JsonPropertyName("total_cases")] public int TotalCases { get; init; }
    [JsonPropertyName("deterministic_cases")] public int DeterministicCases { get; init; }
    [JsonPropertyName("llm_required_cases")] public int LlmRequiredCases { get; init; }
    [JsonPropertyName("ci_gate_threshold")] public double CiGateThreshold { get; init; }
}

public sealed record CostEnvelope
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; init; }
    [JsonPropertyName("usd_estimated")] public double UsdEstimated { get; init; }
    [JsonPropertyName("cap_usd")] public double CapUsd { get; init; }
    [JsonPropertyName("notes")] public string Notes { get; init; } = "";
}

public sealed record SummaryEnvelope
{
    [JsonPropertyName("deterministic_pass")] public int DeterministicPass { get; init; }
    [JsonPropertyName("deterministic_fail")] public int DeterministicFail { get; init; }
    [JsonPropertyName("deterministic_pass_rate")] public double DeterministicPassRate { get; init; }
    [JsonPropertyName("gate_status")] public string GateStatus { get; init; } = "";
}

public sealed record ModelRubricEnvelope
{
    [JsonPropertyName("status")] public string Status { get; init; } = "not-run";
    [JsonPropertyName("note")] public string Note { get; init; } = "";
}
