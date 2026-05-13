using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Agents.Tools;

/// <summary>
/// AI-callable tool that agents invoke when they need human approval
/// before executing a high-impact action. Creates an approval request,
/// pushes a SignalR notification to connected dashboard clients,
/// and blocks until the human responds or the request times out.
/// </summary>
public class ApprovalTool
{
    private readonly IApprovalGate _gate;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<ApprovalTool> _logger;

    public ApprovalTool(
        IApprovalGate gate,
        IHubContext<TelemetryHub> hubContext,
        ILogger<ApprovalTool> logger)
    {
        _gate = gate;
        _hubContext = hubContext;
        _logger = logger;
    }

    [Description(
        "Request human approval before executing a high-impact action. " +
        "Use this when recommending actions with significant cost, revenue, or operational impact " +
        "(e.g., price changes, production adjustments, campaign launches). " +
        "The request is sent to the user and the tool blocks until they Approve, Reject, or Modify.")]
    public async Task<string> RequestApproval(
        [Description("The specific action being proposed, e.g. 'Increase production 20% for Brand X Q4'")] string action,
        [Description("Estimated business impact, e.g. 'Estimated cost: $2.1M, projected revenue lift: $4.8M'")] string impact,
        [Description("Urgency level: 'high', 'medium', or 'low'")] string urgency,
        [Description("Detailed reasoning for why this action is recommended")] string reasoning,
        [Description("The agent making the request, e.g. 'demand-forecasting'")] string agentId,
        [Description("The user who should approve this action")] string userId,
        CancellationToken cancellationToken = default)
    {
        var context = new ApprovalContext(
            AgentId: agentId,
            UserId: userId,
            Action: action,
            Impact: impact,
            Urgency: urgency,
            Reasoning: reasoning);

        try
        {
            // Create the approval request
            var request = await _gate.RequestApprovalAsync(context, cancellationToken);

            _logger.LogInformation(
                "Approval tool created request {RequestId} for action: {Action}",
                request.RequestId, action);

            // Push real-time notification to connected dashboard clients
            await _hubContext.Clients.All.SendAsync(
                "approval_requested",
                new
                {
                    requestId = request.RequestId,
                    agentId = context.AgentId,
                    userId = context.UserId,
                    action = context.Action,
                    impact = context.Impact,
                    urgency = context.Urgency,
                    reasoning = context.Reasoning,
                    createdAt = request.CreatedAt,
                    expiresAt = request.ExpiresAt
                });

            // Block until the human responds or timeout
            var result = await _gate.WaitForApprovalAsync(request.RequestId, timeout: null, cancellationToken);

            // Push resolution notification
            await _hubContext.Clients.All.SendAsync(
                "approval_resolved",
                new
                {
                    requestId = result.RequestId,
                    decision = result.Decision.ToString(),
                    comment = result.Comment,
                    respondedAt = result.RespondedAt
                });

            _logger.LogInformation(
                "Approval {RequestId} resolved as {Decision}",
                result.RequestId, result.Decision);

            return JsonSerializer.Serialize(new
            {
                requestId = result.RequestId,
                decision = result.Decision.ToString(),
                comment = result.Comment,
                respondedAt = result.RespondedAt
            });
        }
        catch (OperationCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                decision = "Cancelled",
                reason = "The approval request was cancelled."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approval tool failed for action: {Action}", action);
            return JsonSerializer.Serialize(new
            {
                decision = "Error",
                reason = $"Failed to process approval request: {ex.Message}"
            });
        }
    }
}
