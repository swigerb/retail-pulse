using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Endpoints;

public static class ApprovalEndpoints
{
    public static WebApplication MapApprovalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/approvals/pending", async (IApprovalGate gate, HttpContext http, CancellationToken ct) =>
        {
            string userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(userId, ct);

            return Results.Ok(pending.Select(r => new
            {
                id = r.RequestId,
                action = r.Context.Action,
                reasoning = r.Context.Reasoning,
                impact = r.Context.Impact,
                urgency = r.Context.Urgency,
                agentId = r.Context.AgentId,
                agentName = r.Context.AgentId,
                requestedAt = r.CreatedAt,
                timeoutAt = r.ExpiresAt,
                status = r.Decision.ToString().ToLowerInvariant(),
                comment = r.Comment
            }));
        })
        .WithName("GetPendingApprovals")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/approvals/{requestId}", async (string requestId, IApprovalGate gate, CancellationToken ct) =>
        {
            try
            {
                ApprovalResult result = await gate.GetResultAsync(requestId, ct);
                return Results.Ok(new
                {
                    requestId = result.RequestId,
                    decision = result.Decision.ToString().ToLowerInvariant(),
                    comment = result.Comment,
                    respondedAt = result.RespondedAt,
                    terminalReason = result.TerminalReason
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
            }
        })
        .WithName("GetApprovalStatus")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapPost("/api/approvals/{requestId}/respond", async (string requestId, ApprovalResponseDto body, IApprovalGate gate, IHubContext<TelemetryHub> hubContext, CancellationToken ct) =>
        {
            if (!Enum.TryParse(body.Decision, true, out ApprovalDecision decision)
                || decision is ApprovalDecision.Pending
                             or ApprovalDecision.TimedOut
                             or ApprovalDecision.Orphaned)
            {
                return Results.BadRequest(new { error = "Decision must be 'Approved', 'Rejected', or 'Modified'." });
            }

            try
            {
                // RespondAsync returns the actual persisted winner. If the conditional
                // Pending → terminal write lost to a timeout, orphan reconciliation, or
                // another human response, we must NOT echo the caller-requested decision
                // — the HTTP response and the SignalR broadcast both report the actual
                // stored outcome so exactly one user-visible resolution is observable.
                ApprovalResult result = await gate.RespondAsync(requestId, decision, body.Comment, ct: ct);

                var payload = new
                {
                    requestId = result.RequestId,
                    decision = result.Decision.ToString().ToLowerInvariant(),
                    comment = result.Comment,
                    respondedAt = result.RespondedAt,
                    terminalReason = result.TerminalReason
                };

                // Notify connected dashboard clients of the resolution using the persisted
                // winner so a lost-race response never advertises a decision that was not
                // recorded.
                await hubContext.Clients.All.SendAsync("approval_resolved", payload);

                return Results.Ok(payload);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Approval request '{requestId}' not found." });
            }
        })
        .WithName("RespondToApproval")
        .RequireAuthorization()
        .RequireRateLimiting("moderate");

        app.MapGet("/api/approvals/history", async (IApprovalGate gate, CancellationToken ct) =>
        {
            IReadOnlyList<ApprovalRequest> history = await gate.GetHistoryAsync(50, ct);

            return Results.Ok(history.Select(r => new
            {
                id = r.RequestId,
                action = r.Context.Action,
                reasoning = r.Context.Reasoning,
                impact = r.Context.Impact,
                urgency = r.Context.Urgency,
                agentId = r.Context.AgentId,
                agentName = r.Context.AgentId,
                requestedAt = r.CreatedAt,
                timeoutAt = r.ExpiresAt,
                status = r.Decision.ToString().ToLowerInvariant(),
                decidedAt = r.RespondedAt,
                comment = r.Comment,
                terminalReason = r.TerminalReason
            }));
        })
        .WithName("GetApprovalHistory")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        return app;
    }
}

record ApprovalResponseDto(string Decision, string? Comment = null);
