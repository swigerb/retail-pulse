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

    public const string FailoverDemo = "x-rp-gw-failover-demo";
    public const string PrimaryStatus = "x-rp-gw-primary-status";
    public const string Attempts = "x-rp-gw-attempts";
    public const string Fallback = "x-rp-gw-fallback";

    public const string SafetyPhase = "x-rp-gw-safety-phase";
    public const string SafetyDecision = "x-rp-gw-safety-decision";
    public const string SafetyPromptAttack = "x-rp-gw-safety-prompt-attack";
    public const string SafetyCategory = "x-rp-gw-safety-category";
    public const string SafetySeverity = "x-rp-gw-safety-severity";
    public const string SafetyMode = "x-rp-gw-safety-mode";
    public const string SafetyLatencyMs = "x-rp-gw-safety-latency-ms";
    public const string SafetyOutputChecked = "x-rp-gw-safety-output-checked";

    public const string BudgetScope = "x-rp-gw-budget-scope";
    public const string BudgetMode = "x-rp-gw-budget-mode";
    public const string RetryAfter = "x-rp-gw-retry-after";

    private const long _maxRetryAfterSeconds = 86_400;

    private static readonly string[] _harmCategories = ["Hate", "SelfHarm", "Sexual", "Violence"];

    private const int _maxTokenLength = 64;
    private const long _maxCount = 1_000_000_000_000;

    public static IReadOnlyList<string> AllNames { get; } =
    [
        RequestId, Backend, Route, TokensConsumed, TokensRemaining, Quota, Cache,
        Safety, PolicyVersion, LatencyMs, BackendLatencyMs, FailoverDemo, PrimaryStatus, Attempts, Fallback,
        SafetyPhase, SafetyDecision, SafetyPromptAttack, SafetyCategory, SafetySeverity, SafetyMode,
        SafetyLatencyMs, SafetyOutputChecked, BudgetScope, BudgetMode, RetryAfter,
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
            statusCode,
            FailoverDemo: ParseFailoverDemo(getHeader),
            GatewaySafety: ParseSafety(getHeader),
            Budget: ParseBudget(getHeader));

        GatewayEvidenceStatus status = evidence.IsEmpty
            ? GatewayEvidenceStatus.Unavailable
            : evidence.Safety == GatewayOutcome.Blocked ? GatewayEvidenceStatus.Blocked : GatewayEvidenceStatus.Observed;
        return evidence with { Status = status };
    }

    // Safety detail is only trusted when the gateway reported a known phase and decision; the
    // category is allow-listed and severity bounded, so no free text can reach the evidence.
    internal static GatewaySafetyEvidence? ParseSafety(Func<string, string?> getHeader)
    {
        GatewaySafetyPhase phase = getHeader(SafetyPhase)?.Trim().ToLowerInvariant() switch
        {
            "input" => GatewaySafetyPhase.Input,
            "output" => GatewaySafetyPhase.Output,
            _ => GatewaySafetyPhase.Unknown,
        };
        GatewaySafetyDecision decision = getHeader(SafetyDecision)?.Trim().ToLowerInvariant() switch
        {
            "allow" => GatewaySafetyDecision.Allow,
            "block" => GatewaySafetyDecision.Block,
            "allowed_on_safety_error" => GatewaySafetyDecision.AllowedOnSafetyError,
            _ => GatewaySafetyDecision.Unknown,
        };
        if (phase == GatewaySafetyPhase.Unknown || decision == GatewaySafetyDecision.Unknown)
        {
            return null;
        }

        string? category = _harmCategories.FirstOrDefault(c => string.Equals(c, getHeader(SafetyCategory)?.Trim(), StringComparison.Ordinal));
        long? severity = ParseCount(getHeader(SafetySeverity));
        string? mode = getHeader(SafetyMode)?.Trim().ToLowerInvariant();
        return new GatewaySafetyEvidence(
            phase,
            decision,
            ParseBool(getHeader(SafetyPromptAttack)),
            category,
            severity is <= 7 ? (int?)severity : null,
            mode is "demo" or "regulated" ? mode : null,
            ParseCount(getHeader(SafetyLatencyMs)),
            ParseBool(getHeader(SafetyOutputChecked)));
    }

    // Budget detail is trusted only when scope kind and mode are both from the fixed vocabulary.
    // Retry-after must be a bounded non-negative integer; anything else is unknown, never defaulted.
    internal static GatewayBudgetEvidence? ParseBudget(Func<string, string?> getHeader)
    {
        GatewayBudgetScopeKind? scope = getHeader(BudgetScope)?.Trim().ToLowerInvariant() switch
        {
            "store" => GatewayBudgetScopeKind.Store,
            "channel" => GatewayBudgetScopeKind.Channel,
            "application" => GatewayBudgetScopeKind.Application,
            "b2b" => GatewayBudgetScopeKind.B2b,
            _ => null,
        };
        GatewayBudgetMode? mode = getHeader(BudgetMode)?.Trim().ToLowerInvariant() switch
        {
            "normal" => GatewayBudgetMode.Normal,
            "demo" => GatewayBudgetMode.Demo,
            _ => null,
        };
        if (scope is null || mode is null)
        {
            return null;
        }

        long? retry = ParseCount(getHeader(RetryAfter));
        return new GatewayBudgetEvidence(scope.Value, mode.Value, retry is <= _maxRetryAfterSeconds ? retry : null);
    }

    internal static bool? ParseBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => null };

    // Demo fields are only trusted behind the explicit "active" marker; without it they are ignored.
    internal static GatewayFailoverDemoEvidence? ParseFailoverDemo(Func<string, string?> getHeader)
    {
        if (!string.Equals(getHeader(FailoverDemo)?.Trim(), "active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        long? primary = ParseCount(getHeader(PrimaryStatus));
        long? attempts = ParseCount(getHeader(Attempts));
        bool? fallback = getHeader(Fallback)?.Trim().ToLowerInvariant() switch
        {
            "used" => true,
            "not-used" => false,
            _ => null,
        };
        return new GatewayFailoverDemoEvidence(
            primary is >= 100 and <= 599 ? (int)primary : null,
            attempts is >= 1 and <= 10 ? (int)attempts : null,
            fallback);
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
