using Microsoft.Extensions.Options;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Endpoints for the durable session/turn store. Mapped only when
/// <see cref="SessionPersistenceOptions.Enabled"/> is true so the API surface does not
/// grow endpoints an operator never opted into (the same convention Anonymous/GitHub
/// auth already use for their opt-in endpoints).
///
/// Every route resolves the caller's subject via <see cref="UserIdentity.Resolve"/> so
/// writes (via <c>/api/chat</c>) and reads share one identity path — the historical
/// "0 memories" drift from Sprint 1 stays fixed. Anonymous principals are refused at
/// endpoint entry, mirroring the cache/memory disable pattern in <c>ChatEndpoints</c>.
/// </summary>
public static class SessionEndpoints
{
    public static WebApplication MapSessionEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/sessions")
            .WithTags("Sessions")
            .RequireAuthorization();

        // GET /api/sessions — list the caller's sessions (newest activity first).
        group.MapGet("/", async (
            ISessionStore store,
            IOptions<SessionPersistenceOptions> options,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            IReadOnlyList<SessionSummaryDto> sessions = await store.ListSessionsForSubjectAsync(subject, ct);

            int cap = Math.Max(1, options.Value.MaxSessionsPerList);
            if (sessions.Count > cap)
                sessions = [.. sessions.Take(cap)];

            return Results.Ok(sessions);
        })
        .WithName("ListSessions")
        .WithSummary("List the caller's persisted chat sessions")
        .Produces<IReadOnlyList<SessionSummaryDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("relaxed");

        // GET /api/sessions/{sessionId} — rehydrate one session, or 404 if not owned.
        group.MapGet("/{sessionId}", async (
            string sessionId,
            ISessionStore store,
            IOptions<SessionPersistenceOptions> options,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            SessionDetailDto? detail = await store.GetSessionAsync(subject, sessionId, ct);
            if (detail is null)
            {
                // Cross-subject reads and truly-unknown ids share the same response so the
                // endpoint cannot be used to probe another subject's session ids.
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }

            int cap = Math.Max(1, options.Value.MaxTurnsPerRehydrate);
            if (detail.Turns.Count > cap)
            {
                // Keep the newest window — the browser gets the last <cap> turns of
                // context so the transcript stays legible without unbounded memory.
                IReadOnlyList<SessionTurnDto> trimmed = [.. detail.Turns.Skip(detail.Turns.Count - cap)];
                detail = detail with { Turns = trimmed };
            }

            return Results.Ok(detail);
        })
        .WithName("GetSession")
        .WithSummary("Rehydrate one persisted chat session for the caller")
        .Produces<SessionDetailDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("relaxed");

        // DELETE /api/sessions/{sessionId} — purge a session and every turn under it.
        group.MapDelete("/{sessionId}", async (
            string sessionId,
            ISessionStore store,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(httpContext, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(httpContext.User);
            bool removed = await store.DeleteSessionAsync(subject, sessionId, ct);
            return removed
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Session '{sessionId}' not found." });
        })
        .WithName("DeleteSession")
        .WithSummary("Delete every persisted turn for one session owned by the caller")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("moderate");

        return app;
    }

    /// <summary>
    /// Anonymous callers never persist and therefore never have anything to read. The
    /// same shape as the chat-endpoint anonymous refusals: 403 with a machine-readable
    /// code, no oracle for whether a given id exists.
    /// </summary>
    private static bool RefuseAnonymous(HttpContext httpContext, out IResult? refusal)
    {
        if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(httpContext.User))
        {
            refusal = Results.Json(
                new { error = "Anonymous sessions are not persisted.", code = "session_persistence_unavailable" },
                statusCode: StatusCodes.Status403Forbidden);
            return true;
        }

        refusal = null;
        return false;
    }
}
