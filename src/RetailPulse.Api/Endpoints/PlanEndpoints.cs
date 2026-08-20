using RetailPulse.Api.Auth;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Endpoints for the durable plan store (issue #93). Mapped only when
/// <see cref="PlanPersistenceOptions.Enabled"/> is true — the same
/// opt-in convention <see cref="SessionEndpoints"/> uses. Anonymous callers
/// are refused at entry; cross-subject reads collapse into a 404 so the
/// endpoint cannot be used to probe another subject's plan ids.
/// </summary>
public static class PlanEndpoints
{
    public static WebApplication MapPlanEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/plans")
            .WithTags("Plans")
            .RequireAuthorization();

        // GET /api/plans — list the caller's plans, newest activity first.
        group.MapGet("/", async (
            IPlanStore store,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            IReadOnlyList<PlanSummaryDto> plans = await store.ListPlansForSubjectAsync(subject, ct);
            return Results.Ok(plans);
        })
        .WithName("ListPlans")
        .WithSummary("List the caller's persisted plans")
        .Produces<IReadOnlyList<PlanSummaryDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden)
        .RequireRateLimiting("relaxed");

        // GET /api/plans/{planId} — rehydrate one plan (with ordered steps), or 404.
        group.MapGet("/{planId}", async (
            string planId,
            IPlanStore store,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            PlanDetailDto? detail = await store.GetPlanAsync(subject, planId, ct);
            return detail is null
                ? Results.NotFound(new { error = $"Plan '{planId}' not found." })
                : Results.Ok(detail);
        })
        .WithName("GetPlan")
        .WithSummary("Rehydrate one persisted plan for the caller")
        .Produces<PlanDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden)
        .RequireRateLimiting("relaxed");

        // DELETE /api/plans/{planId} — remove a plan and every step under it.
        group.MapDelete("/{planId}", async (
            string planId,
            IPlanStore store,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            bool removed = await store.DeletePlanAsync(subject, planId, ct);
            return removed
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Plan '{planId}' not found." });
        })
        .WithName("DeletePlan")
        .WithSummary("Delete a persisted plan owned by the caller")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status403Forbidden)
        .RequireRateLimiting("moderate");

        return app;
    }

    private static bool RefuseAnonymous(HttpContext httpContext, out IResult? refusal)
    {
        if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(httpContext.User))
        {
            refusal = Results.Json(
                new { error = "Anonymous callers do not own plans.", code = "plan_persistence_unavailable" },
                statusCode: StatusCodes.Status403Forbidden);
            return true;
        }

        refusal = null;
        return false;
    }
}
