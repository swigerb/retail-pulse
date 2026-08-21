using RetailPulse.Api.Auth;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// User-initiated execution control (issue #92) — cancel an in-flight run and
/// reconcile durable plan state after a reconnect.
///
/// <para><b>Cancel</b> maps to a subject-scoped
/// <see cref="IExecutionCancellationRegistry"/> entry so the endpoint returns
/// 204 only when the caller owns the scope. Any other outcome — no registration
/// or a foreign owner — collapses to 404 so the endpoint cannot be used to
/// probe another subject's live sessions or plan ids.</para>
///
/// <para><b>Reconcile</b> reads durable plan state (steps + status + tokens)
/// after a reconnect. Ownership is enforced by the plan store, which filters by
/// subject at the SQL layer — a cross-subject plan id resolves to 404.</para>
/// </summary>
public static class ExecutionControlEndpoints
{
    public static WebApplication MapExecutionControlEndpoints(this WebApplication app)
    {
        // POST /api/chat/{sessionId}/cancel — cancels the caller's in-flight
        // fast-path or streaming request keyed on sessionId.
        app.MapPost("/api/chat/{sessionId}/cancel", (
            string sessionId,
            HttpContext http,
            IExecutionCancellationRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new { error = "sessionId is required." });
            }

            string subject = UserIdentity.Resolve(http.User);
            ExecutionCancelResult result = registry.TryCancel(
                ExecutionCancellationRegistry.ChatScope, sessionId, subject);

            return result switch
            {
                ExecutionCancelResult.Cancelled => Results.NoContent(),
                // Forbidden collapses to 404 so the endpoint cannot be used
                // to probe another subject's live sessions.
                ExecutionCancelResult.NotFound or ExecutionCancelResult.Forbidden or _ =>
                    Results.NotFound(new { error = "No in-flight chat run for this session." }),
            };
        })
        .WithName("CancelChat")
        .WithSummary("Cancel the caller's in-flight /api/chat run for this session")
        .WithTags("Chat")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAuthorization()
        .RequireRateLimiting("moderate");

        // POST /api/plans/{planId}/cancel — cancels the caller's in-flight
        // plan orchestration keyed on planId. Anonymous callers are refused
        // (plans are never anonymous — see PlanEndpoints).
        app.MapPost("/api/plans/{planId}/cancel", (
            string planId,
            HttpContext http,
            IExecutionCancellationRegistry registry) =>
        {
            if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(http.User))
            {
                return Results.Json(
                    new { error = "Anonymous callers do not own plans.", code = "plan_cancel_unavailable" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(planId))
            {
                return Results.BadRequest(new { error = "planId is required." });
            }

            string subject = UserIdentity.Resolve(http.User);
            ExecutionCancelResult result = registry.TryCancel(
                ExecutionCancellationRegistry.PlanScope, planId, subject);

            return result switch
            {
                ExecutionCancelResult.Cancelled => Results.NoContent(),
                ExecutionCancelResult.NotFound or ExecutionCancelResult.Forbidden or _ =>
                    Results.NotFound(new { error = $"No in-flight plan '{planId}'." }),
            };
        })
        .WithName("CancelPlan")
        .WithSummary("Cancel the caller's in-flight plan orchestration")
        .WithTags("Plans")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAuthorization()
        .RequireRateLimiting("moderate");

        return app;
    }

    /// <summary>
    /// GET /api/plans/{planId}/reconcile — durable plan state for a
    /// reconnecting client, optionally filtered to steps whose index exceeds
    /// <c>afterStepIndex</c> so the caller can render only the ones it missed.
    /// Requires the plan store; mapped only when plan persistence is enabled.
    /// </summary>
    public static WebApplication MapPlanReconciliationEndpoint(this WebApplication app)
    {
        app.MapGet("/api/plans/{planId}/reconcile", async (
            string planId,
            int? afterStepIndex,
            IPlanStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(http.User))
            {
                return Results.Json(
                    new { error = "Anonymous callers do not own plans.", code = "plan_reconcile_unavailable" },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            string subject = UserIdentity.Resolve(http.User);
            PlanDetailDto? detail = await store.GetPlanAsync(subject, planId, ct);
            if (detail is null)
            {
                // Not found OR cross-subject probe; both collapse to 404 (see
                // SqlitePlanStore for the subject-filter enforcement).
                return Results.NotFound(new { error = $"Plan '{planId}' not found." });
            }

            int cursor = afterStepIndex ?? -1;
            IReadOnlyList<PlanStepRecordDto> missedSteps = [.. detail.Steps.Where(s => s.StepIndex > cursor)];

            return Results.Ok(new
            {
                planId = detail.PlanId,
                sessionId = detail.SessionId,
                status = detail.Status,
                failureReason = detail.FailureReason,
                updatedAt = detail.UpdatedAt,
                totalStepCount = detail.Steps.Count,
                afterStepIndex = cursor,
                steps = missedSteps,
            });
        })
        .WithName("ReconcilePlan")
        .WithSummary("Return durable plan status and any steps the caller has not yet rendered")
        .WithTags("Plans")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        return app;
    }
}
