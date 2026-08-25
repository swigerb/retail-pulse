using System.Text.Json;
using Microsoft.Extensions.Options;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Default <see cref="IPlanClarifier"/> — writes a clarification row through
/// the shared <see cref="IApprovalGate"/> and waits with the configured
/// <see cref="PlanReviewOptions.ClarificationTimeout"/>.
///
/// <para>
/// Exposes both the pre-existing blocking <see cref="AskAsync"/> shape (used
/// by unit tests that respond on a background task) and the non-blocking
/// <see cref="OpenAsync"/> / <see cref="TryReadAnswerAsync"/> primitives the
/// production PlanExecutor / PlanReviewCompletionService use to suspend a
/// running plan and resume it in a fresh process after the reviewer answers.
/// Both paths share the same durable approval row and framework checkpoint so
/// the audit trail is identical.
/// </para>
/// </summary>
public sealed class PlanClarifier : IPlanClarifier
{
    private readonly IApprovalGate _gate;
    private readonly PlanReviewOptions _options;
    private readonly PlanReviewCheckpointService _checkpoints;
    private readonly ILogger<PlanClarifier> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PlanClarifier(
        IApprovalGate gate,
        IOptions<PlanReviewOptions> options,
        PlanReviewCheckpointService checkpoints,
        ILogger<PlanClarifier> logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Configured clarification timeout — exposed for the sweep.</summary>
    public TimeSpan ClarificationTimeout => _options.ClarificationTimeout;

    /// <summary>
    /// Non-blocking suspend point for a mid-plan clarification. Writes a real
    /// framework checkpoint capturing the paused plan state, opens a
    /// <see cref="ApprovalKind.Clarification"/> approval row, then returns the
    /// request id + checkpoint id. The caller (PlanExecutor) unwinds
    /// immediately; the resume path drives execution forward when the answer
    /// arrives via the endpoint or restart recovery.
    /// </summary>
    public async Task<PlanClarificationHandle> OpenAsync(
        PlanClarificationOpenInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Subject);

        var prompt = new PlanClarificationPrompt
        {
            PlanId = input.PlanId,
            StepIndex = input.PausedAtStepIndex,
            SpecialistKey = input.SpecialistKey,
            Question = input.Question,
        };

        var checkpointState = new PlanReviewCheckpointState
        {
            Kind = PlanCheckpointKind.Clarification,
            PlanId = input.PlanId,
            Subject = input.Subject,
            SessionId = input.SessionId,
            TenantId = input.TenantId,
            Request = input.Request,
            RoundNumber = 0,
            Steps = input.RemainingSteps,
            SpecialistKeys = [.. input.SpecialistKeys],
            DetectedIntents = input.DetectedIntents,
            TraceId = input.TraceId,
            ParentSpanId = input.ParentSpanId,
            PrincipalKey = input.PrincipalKey,
            ApprovalRequestId = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            PausedAtStepIndex = input.PausedAtStepIndex,
            PausedStepId = input.PausedStepId,
            CompletedSteps = input.CompletedSteps,
        };

        Microsoft.Agents.AI.Workflows.CheckpointInfo checkpoint =
            await _checkpoints.SaveAsync(checkpointState, ct).ConfigureAwait(false);

        var context = new ApprovalContext(
            AgentId: "plan-clarification",
            UserId: input.Subject,
            Action: $"Answer clarification for step {input.PausedAtStepIndex} ({input.SpecialistKey}).",
            Impact: "Blocking a plan step.",
            Urgency: "medium",
            Reasoning: input.Question,
            SessionId: input.SessionId,
            ConversationId: null,
            Kind: ApprovalKind.Clarification,
            PlanId: input.PlanId,
            RoundNumber: 0,
            Payload: JsonSerializer.Serialize(prompt, _jsonOptions));

        ApprovalRequest request = await _gate.RequestApprovalAsync(context, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Clarification {RequestId} opened for plan {PlanId} step {StepIndex} (checkpoint={CheckpointId}).",
            request.RequestId, input.PlanId, input.PausedAtStepIndex, checkpoint.CheckpointId);

        return new PlanClarificationHandle(request.RequestId, checkpoint.CheckpointId, request.ExpiresAt);
    }

    /// <summary>
    /// Parse the persisted response payload for a clarification row into a
    /// <see cref="PlanClarificationResult"/>. Non-blocking — the caller has
    /// already observed a terminal decision via the endpoint or the row
    /// state.
    /// </summary>
    public static PlanClarificationResult InterpretAnswer(ApprovalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Decision is ApprovalDecision.TimedOut or ApprovalDecision.Orphaned)
        {
            return new PlanClarificationResult(false, null,
                PlanReviewTerminalReason.ReviewTimedOut, result.RequestId);
        }

        if (string.IsNullOrWhiteSpace(result.ResponsePayload))
        {
            return new PlanClarificationResult(false, null,
                PlanReviewTerminalReason.ClarificationInvalid, result.RequestId);
        }

        try
        {
            PlanClarificationAnswer? answer =
                JsonSerializer.Deserialize<PlanClarificationAnswer>(result.ResponsePayload, _jsonOptions);
            return answer is null || string.IsNullOrWhiteSpace(answer.Answer)
                ? new PlanClarificationResult(false, null,
                    PlanReviewTerminalReason.ClarificationInvalid, result.RequestId)
                : new PlanClarificationResult(true, answer.Answer,
                    PlanReviewTerminalReason.ReviewerApproved, result.RequestId);
        }
        catch (JsonException)
        {
            return new PlanClarificationResult(false, null,
                PlanReviewTerminalReason.ClarificationInvalid, result.RequestId);
        }
    }

    public async Task<PlanClarificationResult> AskAsync(
        PlanClarificationPrompt prompt,
        string subject,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var context = new ApprovalContext(
            AgentId: "plan-clarification",
            UserId: subject,
            Action: $"Answer clarification for step {prompt.StepIndex} ({prompt.SpecialistKey}).",
            Impact: "Blocking a plan step.",
            Urgency: "medium",
            Reasoning: prompt.Question,
            SessionId: null,
            ConversationId: null,
            Kind: ApprovalKind.Clarification,
            PlanId: prompt.PlanId,
            RoundNumber: 0,
            Payload: JsonSerializer.Serialize(prompt, _jsonOptions));

        ApprovalRequest request = await _gate.RequestApprovalAsync(context, ct);
        _logger.LogInformation(
            "Clarification {RequestId} opened for plan {PlanId} step {StepIndex}.",
            request.RequestId, prompt.PlanId, prompt.StepIndex);

        ApprovalResult result = await _gate.WaitForApprovalAsync(
            request.RequestId, _options.ClarificationTimeout, ct);

        return InterpretAnswer(result);
    }
}

/// <summary>
/// Envelope passed to <see cref="PlanClarifier.OpenAsync"/> capturing every
/// field a fresh process needs to resume the plan when the reviewer answers.
/// </summary>
public sealed record PlanClarificationOpenInput
{
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public string? SessionId { get; init; }
    public string? TenantId { get; init; }
    public required string Request { get; init; }
    public required string SpecialistKey { get; init; }
    public required string Question { get; init; }
    public required int PausedAtStepIndex { get; init; }
    /// <summary>
    /// Persisted step id (from <see cref="PlanExecutionRequest.StepIds"/>)
    /// of the paused step. Threaded through the checkpoint so the resume
    /// path can transition that specific row from Pending to Completed
    /// once the reviewer answers (finding 1b, #145). Optional for
    /// existing callers that don't yet supply it.
    /// </summary>
    public string? PausedStepId { get; init; }
    public required IReadOnlyList<PlanReviewStepDto> RemainingSteps { get; init; }
    public required IReadOnlyCollection<string> SpecialistKeys { get; init; }
    public IReadOnlyList<string> DetectedIntents { get; init; } = [];
    public IReadOnlyList<PlanReviewCompletedStep>? CompletedSteps { get; init; }
    public string? TraceId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? PrincipalKey { get; init; }
}

/// <summary>Handle returned from <see cref="PlanClarifier.OpenAsync"/>.</summary>
public sealed record PlanClarificationHandle(
    string RequestId,
    string CheckpointId,
    DateTimeOffset ExpiresAt);
