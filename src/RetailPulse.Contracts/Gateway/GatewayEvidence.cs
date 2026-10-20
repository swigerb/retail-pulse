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
/// How trustworthy the gateway evidence for a call (or a whole turn) is.
/// <see cref="Observed"/> = APIM headers were present and trusted; <see cref="Unavailable"/> =
/// APIM is configured but returned no usable evidence; <see cref="Bypassed"/> = the client is
/// not configured for an APIM gateway so no header is trusted; <see cref="Blocked"/> = the gateway
/// reported a content-safety block; <see cref="Failed"/> = the call failed at the transport.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GatewayEvidenceStatus>))]
public enum GatewayEvidenceStatus
{
    [JsonStringEnumMemberName("unavailable")] Unavailable = 0,
    [JsonStringEnumMemberName("observed")] Observed,
    [JsonStringEnumMemberName("bypassed")] Bypassed,
    [JsonStringEnumMemberName("blocked")] Blocked,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>
/// Observed attempts of an authorized failover demonstration (#330). Present only when APIM
/// reported <c>x-rp-gw-failover-demo: active</c>; every field is a status the gateway
/// actually saw, and a field it did not report stays null (unknown) rather than defaulted.
/// <see cref="PrimaryStatusCode"/> is the real primary backend response; the demo does not
/// trip the circuit breaker, so no breaker outcome is claimed.
/// </summary>
public sealed record GatewayFailoverDemoEvidence(int? PrimaryStatusCode, int? Attempts, bool? FallbackUsed);

/// <summary>
/// Privacy-safe evidence of the decisions APIM made for one gateway call. Carries no
/// subscription identifiers, secrets, prompts, completions, backend credentials, or
/// exception text. <see cref="TransportError"/> is a fixed error class only.
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
    int? StatusCode = null,
    GatewayEvidenceStatus Status = GatewayEvidenceStatus.Observed,
    string? TransportError = null,
    GatewayFailoverDemoEvidence? FailoverDemo = null)
{
    /// <summary>True when the response carried no gateway evidence at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        GatewayRequestId is null && BackendAlias is null && Route == GatewayRouteState.Unknown
        && TokensConsumed is null && TokensRemaining is null && Quota == GatewayOutcome.Unknown
        && Cache == GatewayOutcome.Unknown && Safety == GatewayOutcome.Unknown
        && PolicyVersion is null && GatewayLatencyMs is null && BackendLatencyMs is null;

    public static GatewayCallEvidence Empty(GatewayEvidenceStatus status, int? statusCode = null, string? transportError = null) =>
        new(null, null, GatewayRouteState.Unknown, null, null, GatewayOutcome.Unknown, GatewayOutcome.Unknown,
            GatewayOutcome.Unknown, null, null, null, statusCode, status, transportError);
}

/// <summary>
/// Evidence for one chat turn: the retained gateway calls (router, specialist, planner, council)
/// plus an aggregate. <see cref="Status"/> is Observed only when every call was observed;
/// Failed/Blocked win over the rest, then Unavailable, then Bypassed. <see cref="CallCount"/>
/// counts every call made, so <see cref="Truncated"/> is true when it exceeds the retained list.
/// </summary>
public sealed record GatewayEvidence(
    GatewayEvidenceStatus Status,
    int CallCount,
    long? TotalTokensConsumed,
    long? TokensRemaining,
    IReadOnlyList<GatewayCallEvidence> Calls,
    bool Truncated = false,
    int DroppedCalls = 0)
{
    /// <summary>No gateway call was made (or recorded) during the turn.</summary>
    public static GatewayEvidence None { get; } = new(GatewayEvidenceStatus.Unavailable, 0, null, null, []);

    public bool Observed => Status == GatewayEvidenceStatus.Observed;
}
