using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Shapes the client-facing error for a failed gateway call. When APIM answered with a sanitized
/// gateway-safety decision (blocked 403, rejected 400, or generic safety 503) the error keeps a
/// distinct code and the allow-listed evidence instead of collapsing into <c>ai_service_error</c>.
/// Only fields already sanitized by <see cref="GatewayEvidenceHeaders"/> are returned; the dependency
/// body, prompt, completion and documents are never read.
/// </summary>
public static class GatewayErrorShaper
{
    public const string Blocked = "gateway_safety_blocked";
    public const string Rejected = "gateway_safety_rejected";
    public const string Unavailable = "gateway_safety_unavailable";

    public const string TokenBudgetExceeded = "gateway_token_budget_exceeded";

    public sealed record RateLimited(string Code, string Message, long? RetryAfterSeconds, GatewayEvidence? Gateway);

    /// <summary>
    /// Shapes a 429. A throttle that APIM attributed to a token budget keeps a distinct code, the scope kind,
    /// the numeric retry-after and the allow-listed evidence; any other 429 keeps the generic rate_limited code.
    /// </summary>
    public static RateLimited ClassifyRateLimit(GatewayEvidence? snapshot)
    {
        GatewayCallEvidence? call = snapshot?.Calls.LastOrDefault(c =>
            c.StatusCode == 429 && c.Quota == GatewayOutcome.Throttled && c.Budget is not null);
        if (call?.Budget is not { } budget)
        {
            return new RateLimited("rate_limited", "The AI service is experiencing high demand. Please wait 30 seconds and try again.", null, snapshot);
        }

        string wait = budget.RetryAfterSeconds is { } s ? $"Retry in {s} seconds." : "Retry shortly.";
        string mode = budget.Mode == GatewayBudgetMode.Demo ? " (demo limit)" : string.Empty;
        return new RateLimited(
            TokenBudgetExceeded,
            $"The token budget for this {budget.ScopeKind.ToString().ToLowerInvariant()} scope is exhausted{mode}. {wait}",
            budget.RetryAfterSeconds,
            snapshot);
    }

    public sealed record Shaped(string Code, string Message, GatewayEvidence? Gateway);

    public static Shaped Classify(int status, string fallbackCode, string fallbackMessage, GatewayEvidence? snapshot)
    {
        GatewayCallEvidence? call = snapshot?.Calls.LastOrDefault(c =>
            c.Status == GatewayEvidenceStatus.Blocked
            && c.StatusCode == status
            && c.GatewaySafety is { Decision: GatewaySafetyDecision.Block });
        return call is null
            ? new Shaped(fallbackCode, fallbackMessage, snapshot)
            : status switch
            {
                403 => new Shaped(Blocked, "The request was blocked by gateway content safety.", snapshot),
                400 => new Shaped(Rejected, "The request could not be verified by gateway content safety.", snapshot),
                503 => new Shaped(Unavailable, "Content safety could not be verified.", snapshot),
                _ => new Shaped(fallbackCode, fallbackMessage, snapshot),
            };
    }
}
