namespace RetailPulse.Api.Guardrails.ContentSafety;

/// <summary>
/// Second-layer content safety evaluator. The pattern layer runs first and may
/// short-circuit; only when it passes does the middleware / RAG / tool-result
/// path call this evaluator.
/// </summary>
/// <remarks>
/// Implementations must be side-effect free with respect to the underlying HTTP
/// call — retries, timeouts, and circuit-breaker semantics are supplied by the
/// registered resilience pipeline, not by callers.
/// </remarks>
public interface IContentSafetyEvaluator
{
    /// <summary>
    /// Evaluate <paramref name="text"/> for the given <paramref name="stage"/>.
    /// Callers translate the resulting decision into a middleware / RAG-drop /
    /// tool-result outcome and audit rows.
    /// </summary>
    Task<ContentSafetyResult> EvaluateAsync(
        string text,
        ContentSafetyStage stage,
        ContentSafetyEvaluationContext context,
        CancellationToken cancellationToken);
}

/// <summary>Location in the request pipeline where the evaluator is invoked.</summary>
public enum ContentSafetyStage
{
    /// <summary>User input at the middleware boundary (after pattern layer passes).</summary>
    Input,

    /// <summary>Model output before it reaches the response body (after PII redaction).</summary>
    Output,

    /// <summary>A single retrieved-knowledge chunk before injection into the prompt.</summary>
    RetrievedKnowledge,

    /// <summary>A tool result before it enters model context.</summary>
    ToolResult,

    /// <summary>
    /// Deployment-time agent definition text (system prompt, display name,
    /// fallback reply, keyword fast paths). Evaluated once at host startup by
    /// the <c>AgentDefinitionValidator</c> introduced in issue #99.
    /// </summary>
    AgentDefinition,
}

/// <summary>Tenant/principal/source metadata used for audit rows only.</summary>
public sealed record ContentSafetyEvaluationContext(
    string UserId,
    string? TenantId = null,
    string? SourceId = null,
    bool CheckPromptShield = false);

/// <summary>Terminal decision returned by the evaluator.</summary>
public enum ContentSafetyDecision
{
    /// <summary>Content is safe under every configured threshold.</summary>
    Passed,

    /// <summary>A category exceeded its threshold or Prompt Shields flagged the input.</summary>
    Blocked,

    /// <summary>Content is under thresholds but flagged; caller may audit but not block.</summary>
    Flagged,

    /// <summary>The remote layer was unreachable — caller applies the configured fail policy.</summary>
    ServiceUnavailable,
}

/// <summary>
/// Why a <see cref="ContentSafetyDecision.ServiceUnavailable"/> outcome happened.
/// The evaluator previously collapsed every failure into one generic outcome, so
/// an operator reading the audit trail could not tell a cold-start timeout from a
/// 401 or a dropped connection. Carrying the class lets the audit reason name it.
/// </summary>
public enum ContentSafetyFailureReason
{
    /// <summary>The call did not complete inside its time budget.</summary>
    Timeout,

    /// <summary>Managed-identity authentication was rejected (for example 401/403).</summary>
    Authentication,

    /// <summary>The connection failed before a response (DNS, TLS, socket, 5xx).</summary>
    Transport,

    /// <summary>The resilience circuit breaker was open and short-circuited the call.</summary>
    CircuitOpen,
}

/// <summary>Structured decision + evidence used for auditing and telemetry.</summary>
public sealed record ContentSafetyResult(
    ContentSafetyDecision Decision,
    IReadOnlyList<ContentSafetyCategoryHit> Categories,
    bool PromptShieldJailbreakDetected,
    bool PromptShieldIndirectInjectionDetected,
    TimeSpan Latency,
    string? CorrelationId,
    string? PrimaryCategory = null,
    ContentSafetyFailureReason? FailureReason = null)
{
    /// <summary>Cached passed-with-no-hits singleton returned by the no-op evaluator.</summary>
    public static readonly ContentSafetyResult Passed = new(
        ContentSafetyDecision.Passed,
        [],
        PromptShieldJailbreakDetected: false,
        PromptShieldIndirectInjectionDetected: false,
        Latency: TimeSpan.Zero,
        CorrelationId: null);

    /// <summary>Cached service-unavailable result for downstream fail policy translation.</summary>
    public static readonly ContentSafetyResult ServiceUnavailable = new(
        ContentSafetyDecision.ServiceUnavailable,
        [],
        PromptShieldJailbreakDetected: false,
        PromptShieldIndirectInjectionDetected: false,
        Latency: TimeSpan.Zero,
        CorrelationId: null);

    /// <summary>
    /// Service-unavailable result carrying the failure class so the audit reason
    /// can distinguish a timeout from an auth or transport failure.
    /// </summary>
    public static ContentSafetyResult Unavailable(ContentSafetyFailureReason reason) => new(
        ContentSafetyDecision.ServiceUnavailable,
        [],
        PromptShieldJailbreakDetected: false,
        PromptShieldIndirectInjectionDetected: false,
        Latency: TimeSpan.Zero,
        CorrelationId: null,
        PrimaryCategory: null,
        FailureReason: reason);
}

/// <summary>A single category hit from text moderation.</summary>
public sealed record ContentSafetyCategoryHit(string Category, int Severity);
