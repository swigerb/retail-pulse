using System.Text.Json.Serialization;

namespace RetailPulse.Contracts.Gateway;

/// <summary>
/// Outcome vocabulary shared by every gateway decision. <see cref="Unknown"/> means the
/// gateway should have reported the field but it was absent or malformed;
/// <see cref="NotApplicable"/> means the gateway explicitly said the decision does not
/// apply. Neither may ever be rendered as success.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GatewayOutcome>))]
public enum GatewayOutcome
{
    [JsonStringEnumMemberName("unknown")] Unknown = 0,
    [JsonStringEnumMemberName("not-applicable")] NotApplicable,
    [JsonStringEnumMemberName("allowed")] Allowed,
    [JsonStringEnumMemberName("throttled")] Throttled,
    [JsonStringEnumMemberName("hit")] Hit,
    [JsonStringEnumMemberName("miss")] Miss,
    [JsonStringEnumMemberName("passed")] Passed,
    [JsonStringEnumMemberName("blocked")] Blocked,
}

[JsonConverter(typeof(JsonStringEnumConverter<GatewayRouteState>))]
public enum GatewayRouteState
{
    [JsonStringEnumMemberName("unknown")] Unknown = 0,
    [JsonStringEnumMemberName("primary")] Primary,
    [JsonStringEnumMemberName("failover")] Failover,
}

/// <summary>
/// Privacy-safe evidence of the decisions APIM made for one gateway call. Carries no
/// subscription identifiers, secrets, prompts, completions, or backend credentials.
/// </summary>
public sealed record GatewayCallEvidence(
    string? GatewayRequestId,
    string? BackendAlias,
    GatewayRouteState Route,
    long? TokensConsumed,
    long? TokensRemaining,
    GatewayOutcome Quota,
    GatewayOutcome Cache,
    GatewayOutcome Safety,
    string? PolicyVersion,
    long? GatewayLatencyMs,
    long? BackendLatencyMs,
    int? StatusCode = null)
{
    /// <summary>True when the response carried no gateway evidence at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        GatewayRequestId is null && BackendAlias is null && Route == GatewayRouteState.Unknown
        && TokensConsumed is null && TokensRemaining is null && Quota == GatewayOutcome.Unknown
        && Cache == GatewayOutcome.Unknown && Safety == GatewayOutcome.Unknown
        && PolicyVersion is null && GatewayLatencyMs is null && BackendLatencyMs is null;
}

/// <summary>
/// Evidence for one chat turn: every gateway call (router, specialist, planner, council)
/// plus an aggregate. <see cref="Observed"/> is false when no call reported evidence, so
/// clients can distinguish "no gateway data" from "gateway said nothing was wrong".
/// </summary>
public sealed record GatewayEvidence(
    bool Observed,
    int CallCount,
    long? TotalTokensConsumed,
    long? TokensRemaining,
    IReadOnlyList<GatewayCallEvidence> Calls)
{
    public static GatewayEvidence None { get; } = new(false, 0, null, null, []);
}
