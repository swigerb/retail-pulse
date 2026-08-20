using System.Text.Json;
using Microsoft.Extensions.Options;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Default <see cref="IPlanClarifier"/> — writes a clarification row through
/// the shared <see cref="IApprovalGate"/> and waits with the configured
/// <see cref="PlanReviewOptions.ClarificationTimeout"/>.
/// </summary>
public sealed class PlanClarifier : IPlanClarifier
{
    private readonly IApprovalGate _gate;
    private readonly PlanReviewOptions _options;
    private readonly ILogger<PlanClarifier> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PlanClarifier(
        IApprovalGate gate,
        IOptions<PlanReviewOptions> options,
        ILogger<PlanClarifier> logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        if (result.Decision is ApprovalDecision.TimedOut or ApprovalDecision.Orphaned)
        {
            return new PlanClarificationResult(false, null, PlanReviewTerminalReason.ReviewTimedOut, request.RequestId);
        }

        if (string.IsNullOrWhiteSpace(result.ResponsePayload))
        {
            return new PlanClarificationResult(
                false, null, PlanReviewTerminalReason.ClarificationInvalid, request.RequestId);
        }

        try
        {
            PlanClarificationAnswer? answer =
                JsonSerializer.Deserialize<PlanClarificationAnswer>(result.ResponsePayload, _jsonOptions);
            return answer is null || string.IsNullOrWhiteSpace(answer.Answer)
                ? new PlanClarificationResult(false, null, PlanReviewTerminalReason.ClarificationInvalid, request.RequestId)
                : new PlanClarificationResult(true, answer.Answer, PlanReviewTerminalReason.ReviewerApproved, request.RequestId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Clarification {RequestId} response payload was not valid JSON.", request.RequestId);
            return new PlanClarificationResult(
                false, null, PlanReviewTerminalReason.ClarificationInvalid, request.RequestId);
        }
    }
}
