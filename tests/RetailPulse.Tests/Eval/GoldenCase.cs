using System.Text.Json.Serialization;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Deterministic expectations for a golden case. Every property listed here can be
/// verified without any live model call: chart-type extraction, explicit-chart
/// detection, the router keyword fast-path, and the memory-command classifier are
/// all pure regex/string operations. Model-graded properties (refusal quality,
/// clarification quality, retrieval fidelity) are reported separately by the harness
/// and never gate CI on their own.
/// </summary>
public sealed record GoldenExpectations
{
    [JsonPropertyName("explicit_chart")]
    public bool ExplicitChart { get; init; }

    [JsonPropertyName("chart_type")]
    public string? ChartType { get; init; }

    /// <summary>"keyword-fast-path" or "llm-required".</summary>
    [JsonPropertyName("routing_mode")]
    public string RoutingMode { get; init; } = "";

    [JsonPropertyName("routing_intent")]
    public string? RoutingIntent { get; init; }

    [JsonPropertyName("expected_llm_call")]
    public bool ExpectedLlmCall { get; init; }

    [JsonPropertyName("refusal_expected")]
    public bool RefusalExpected { get; init; }

    [JsonPropertyName("requires_clarification")]
    public bool RequiresClarification { get; init; }

    [JsonPropertyName("retrieval_expected")]
    public bool RetrievalExpected { get; init; }

    [JsonPropertyName("retrieval_source")]
    public string? RetrievalSource { get; init; }

    [JsonPropertyName("memory_command")]
    public bool MemoryCommand { get; init; }
}

/// <summary>One curated retail prompt with its deterministic expectations.</summary>
public sealed record GoldenCase
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = "";

    [JsonPropertyName("expectations")]
    public GoldenExpectations Expectations { get; init; } = new();
}

/// <summary>Top-level golden dataset envelope loaded from JSON.</summary>
public sealed record GoldenDataset
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("generated_at_local")]
    public string GeneratedAtLocal { get; init; } = "";

    [JsonPropertyName("harness_scope")]
    public string HarnessScope { get; init; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = "";

    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonPropertyName("cases")]
    public IReadOnlyList<GoldenCase> Cases { get; init; } = [];
}
