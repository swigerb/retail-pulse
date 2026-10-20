using System.ClientModel.Primitives;
using System.Globalization;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Maps the APIM-generated <c>x-rp-gw-*</c> response headers to the typed
/// <see cref="GatewayCallEvidence"/>. Only an allow-list of headers is read, values are
/// length- and charset-bounded, and anything missing or malformed becomes an explicit
/// unknown state rather than a fabricated success.
/// </summary>
public static class GatewayEvidenceHeaders
{
    public const string Prefix = "x-rp-gw-";
    public const string RequestId = "x-rp-gw-request-id";
    public const string Backend = "x-rp-gw-backend";
    public const string Route = "x-rp-gw-route";
    public const string TokensConsumed = "x-rp-gw-tokens-consumed";
    public const string TokensRemaining = "x-rp-gw-tokens-remaining";
    public const string Quota = "x-rp-gw-quota";
    public const string Cache = "x-rp-gw-cache";
    public const string Safety = "x-rp-gw-safety";
    public const string PolicyVersion = "x-rp-gw-policy-version";
    public const string LatencyMs = "x-rp-gw-latency-ms";
    public const string BackendLatencyMs = "x-rp-gw-backend-latency-ms";

    private const int _maxTokenLength = 64;
    private const long _maxCount = 1_000_000_000_000;

    public static IReadOnlyList<string> AllNames { get; } =
    [
        RequestId, Backend, Route, TokensConsumed, TokensRemaining, Quota, Cache,
        Safety, PolicyVersion, LatencyMs, BackendLatencyMs,
    ];

    public static GatewayCallEvidence Parse(PipelineResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Parse(name => response.Headers.TryGetValue(name, out string? v) ? v : null, response.Status);
    }

    public static GatewayCallEvidence Parse(Func<string, string?> getHeader, int? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(getHeader);
        var evidence = new GatewayCallEvidence(
            SafeToken(getHeader(RequestId)),
            SafeToken(getHeader(Backend)),
            ParseRoute(getHeader(Route)),
            ParseCount(getHeader(TokensConsumed)),
            ParseCount(getHeader(TokensRemaining)),
            ParseOutcome(getHeader(Quota), GatewayOutcome.Allowed, GatewayOutcome.Throttled),
            ParseOutcome(getHeader(Cache), GatewayOutcome.Hit, GatewayOutcome.Miss),
            ParseOutcome(getHeader(Safety), GatewayOutcome.Passed, GatewayOutcome.Blocked),
            SafeToken(getHeader(PolicyVersion)),
            ParseCount(getHeader(LatencyMs)),
            ParseCount(getHeader(BackendLatencyMs)),
            statusCode);

        GatewayEvidenceStatus status = evidence.IsEmpty
            ? GatewayEvidenceStatus.Unavailable
            : evidence.Safety == GatewayOutcome.Blocked ? GatewayEvidenceStatus.Blocked : GatewayEvidenceStatus.Observed;
        return evidence with { Status = status };
    }

    internal static string? SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string v = value.Trim();
        if (v.Length > _maxTokenLength)
        {
            return null;
        }

        foreach (char c in v)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            {
                return null;
            }
        }

        return v;
    }

    internal static long? ParseCount(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n <= _maxCount
            ? n
            : null;

    internal static GatewayRouteState ParseRoute(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "primary" => GatewayRouteState.Primary,
            "failover" => GatewayRouteState.Failover,
            _ => GatewayRouteState.Unknown,
        };

    internal static GatewayOutcome ParseOutcome(string? value, GatewayOutcome a, GatewayOutcome b)
    {
        GatewayOutcome parsed = value?.Trim().ToLowerInvariant() switch
        {
            "not-applicable" => GatewayOutcome.NotApplicable,
            "allowed" => GatewayOutcome.Allowed,
            "throttled" => GatewayOutcome.Throttled,
            "hit" => GatewayOutcome.Hit,
            "miss" => GatewayOutcome.Miss,
            "passed" => GatewayOutcome.Passed,
            "blocked" => GatewayOutcome.Blocked,
            _ => GatewayOutcome.Unknown,
        };

        // Only the vocabulary valid for this field is accepted.
        return parsed is GatewayOutcome.Unknown or GatewayOutcome.NotApplicable || parsed == a || parsed == b
            ? parsed
            : GatewayOutcome.Unknown;
    }
}
