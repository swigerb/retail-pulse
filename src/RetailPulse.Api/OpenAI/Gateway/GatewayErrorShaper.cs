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

    public sealed record Shaped(string Code, string Message, GatewayEvidence? Gateway);

    public static Shaped Classify(int status, string fallbackCode, string fallbackMessage, GatewayEvidence? snapshot)
    {
        GatewayCallEvidence? call = snapshot?.Calls.LastOrDefault(c =>
            c.Status == GatewayEvidenceStatus.Blocked
            && c.StatusCode == status
            && c.GatewaySafety is { Decision: GatewaySafetyDecision.Block });
        if (call is null)
        {
            return new Shaped(fallbackCode, fallbackMessage, snapshot);
        }

        return status switch
        {
            403 => new Shaped(Blocked, "The request was blocked by gateway content safety.", snapshot),
            400 => new Shaped(Rejected, "The request could not be verified by gateway content safety.", snapshot),
            503 => new Shaped(Unavailable, "Content safety could not be verified.", snapshot),
            _ => new Shaped(fallbackCode, fallbackMessage, snapshot),
        };
    }
}
