using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Approval;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Hubs;
using RetailPulse.Api.Middleware;
using RetailPulse.Api.Persistence;
using RetailPulse.Api.Security.Anonymous;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// HTTP endpoints for plan review (#94). Mirrors the plan-endpoint auth
/// posture: anonymous callers cannot decide plan reviews, and cross-subject
/// decisions collapse into a 404 so the endpoint cannot be used to probe or
/// influence another subject's plan. Every decision is persisted through the
/// shared <see cref="IApprovalGate"/> so plan and tool approvals share one
/// audit trail (#91).
/// </summary>
public static class PlanReviewEndpoints
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WebApplication MapPlanReviewEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/plans/{planId}/reviews")
            .WithTags("Plan Review")
            .RequireAuthorization();

        // GET /api/plans/{planId}/reviews — list open reviews owned by the caller.
        group.MapGet("/", async (
            string planId,
            IApprovalGate gate,
            IPlanStore planStore,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(http, out IResult? refusal))
                return refusal;

            string subject = UserIdentity.Resolve(http.User);
            if (!await OwnsPlanAsync(planStore, subject, planId, ct))
                return NotFound(planId);

            IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject, ct);
            return Results.Ok(
                pending
                    .Where(r => string.Equals(r.Context.PlanId, planId, StringComparison.Ordinal)
                                && string.Equals(r.Context.Kind, ApprovalKind.PlanReview, StringComparison.Ordinal))
                    .Select(ToListDto)
                    .ToArray());
        })
        .WithName("ListPlanReviews")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("relaxed");

        // POST /api/plans/{planId}/reviews/{requestId}/decision — approve/reject/edit.
        group.MapPost("/{requestId}/decision", async (
            string planId,
            string requestId,
            [FromBody] PlanReviewDecisionRequest body,
            IApprovalGate gate,
            IPlanStore planStore,
            IHubContext<TelemetryHub> hubContext,
            HttpContext http,
            [FromServices] PlanReviewCompletionService? completion,
            [FromServices] GuardrailsMiddleware guardrails,
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(http, out IResult? refusal))
                return refusal;

            if (body is null || string.IsNullOrWhiteSpace(body.Kind))
                return Results.BadRequest(new { error = "Decision kind is required." });

            string subject = UserIdentity.Resolve(http.User);
            if (!await OwnsPlanAsync(planStore, subject, planId, ct))
                return NotFound(planId);

            // Look up the row before deciding so we can enforce subject ownership.
            ApprovalRequest? row;
            try
            {
                ApprovalResult existing = await gate.GetResultAsync(requestId, ct);
                // Fetch full history/pending metadata to obtain subject & plan id
                // (RespondAsync's result does not include the context).
                row = await FindRowAsync(gate, subject, requestId, ct);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Plan review not found." });
            }

            if (row is null ||
                !string.Equals(row.Context.UserId, subject, StringComparison.Ordinal) ||
                !string.Equals(row.Context.PlanId, planId, StringComparison.Ordinal) ||
                !string.Equals(row.Context.Kind, ApprovalKind.PlanReview, StringComparison.Ordinal))
            {
                // Cross-subject probe, wrong plan, or wrong kind — return 404
                // so the endpoint cannot be used to probe another subject's
                // plans. Row is left untouched: we never invoke RespondAsync
                // on a row the caller does not own.
                return Results.NotFound(new { error = "Plan review not found." });
            }

            (ApprovalDecision decision, string kind, IReadOnlyList<PlanReviewStepDto>? editedSteps, string? feedback, string? invalid)
                = ClassifyDecision(body);
            if (invalid is not null)
                return Results.BadRequest(new { error = invalid });

            // Guardrail-scan reviewer edits (#97): every edited step's Action is
            // user-supplied free-form text that reaches a specialist verbatim
            // during resume. The initial /api/chat call runs the raw prompt
            // through GuardrailsMiddleware.CheckInputAsync, but the edit path
            // did not — a reviewer could inject a jailbreak/prompt-override
            // instruction through the edit field and bypass the input
            // guardrails entirely. Run each action through the same input
            // gate; on block, refuse the decision without ever calling
            // RespondAsync so no approval row transitions to "modified" from
            // a blocked edit.
            if (editedSteps is { Count: > 0 })
            {
                foreach (PlanReviewStepDto step in editedSteps)
                {
                    string action = step.Action ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(action)) continue;
                    var probe = new ChatRequest(
                        Message: action,
                        SessionId: row.Context.SessionId,
                        User: new UserContext(subject, subject, string.Empty));
                    GuardrailResult guardrailResult = await guardrails.CheckInputAsync(probe, ct);
                    if (guardrailResult.IsBlocked)
                    {
                        return Results.BadRequest(new
                        {
                            error = "Edited step action was blocked by input guardrails.",
                            code = "plan_review_edit_blocked",
                            specialistKey = step.SpecialistKey,
                            refusal = guardrailResult.RefusalMessage,
                        });
                    }
                }
            }

            // Guardrail-scan reviewer rejection feedback (#136): non-empty
            // Feedback flows through PlanReviewCompletionService ->
            // PlanReviewCoordinator.ReplanAsync -> PlanBuilder as part of the
            // revised planner prompt. That's another reviewer-authored user-
            // text ingress that must share the same GuardrailsMiddleware seam
            // as /api/chat and the edit path — otherwise a hostile reviewer
            // could smuggle a jailbreak/prompt-override into the replanner
            // via the feedback field. On block, refuse the decision without
            // ever calling RespondAsync so the approval row stays Pending.
            if (!string.IsNullOrWhiteSpace(feedback))
            {
                var feedbackProbe = new ChatRequest(
                    Message: feedback,
                    SessionId: row.Context.SessionId,
                    User: new UserContext(subject, subject, string.Empty));
                GuardrailResult feedbackGuardrail = await guardrails.CheckInputAsync(feedbackProbe, ct);
                if (feedbackGuardrail.IsBlocked)
                {
                    return Results.BadRequest(new
                    {
                        error = "Rejection feedback was blocked by input guardrails.",
                        code = "plan_review_feedback_blocked",
                        refusal = feedbackGuardrail.RefusalMessage,
                    });
                }
            }

            var payload = new PlanReviewResponsePayload
            {
                Kind = kind,
                EditedSteps = editedSteps,
                Feedback = feedback,
            };

            string payloadJson = JsonSerializer.Serialize(payload, _jsonOptions);

            ApprovalResult result = await gate.RespondAsync(
                requestId,
                decision,
                comment: string.IsNullOrWhiteSpace(feedback) ? body.Comment : feedback,
                responsePayload: payloadJson,
                ct: ct);

            // Build the response DTO from the PERSISTED WINNER, not the
            // caller's own body. When two callers race the same approval row
            // RespondAsync collapses them to a single terminal outcome; the
            // losing caller would otherwise broadcast its own requested
            // `kind` / `feedback` even though the row records a different
            // decision. Resolving `kind` and `feedback` back from the
            // persisted <see cref="ApprovalResult.ResponsePayload"/> keeps
            // the HTTP body, the SignalR broadcast, and the durable row
            // reporting exactly one user-visible decision end-to-end.
            //
            // Fail-safe integrity (#144 follow-up): NEVER fall back to the
            // requesting caller's `kind` or `feedback` on payload
            // missing/malformed, because on a lost race those describe an
            // outcome the durable row does not record. `Kind` is derived from
            // <see cref="ApprovalResult.Decision"/>, which is the row's
            // authoritative outcome; `Feedback` is only surfaced when the
            // persisted payload parses cleanly. When the payload is
            // missing/malformed we omit feedback entirely and skip the
            // resolution broadcast so a losing caller cannot leak its own
            // requested feedback text.
            (string persistedKind, string? persistedFeedback, bool payloadParsed) =
                ExtractWinner(result);
            var responseDto = new
            {
                requestId = result.RequestId,
                planId,
                decision = result.Decision.ToString().ToLowerInvariant(),
                kind = persistedKind,
                comment = result.Comment,
                respondedAt = result.RespondedAt,
                terminalReason = result.TerminalReason,
                round = row.Context.RoundNumber,
                feedback = persistedFeedback,
            };

            // Session-scoped delivery (#141): the plan_review_resolved event
            // carries the plan id, reviewer comment, and terminal reason for a
            // subject-owned plan, so it MUST reach only the owning session's
            // group — never Clients.All. Missing / whitespace session id
            // fails closed (suppress + log) so a regression that lets a plan
            // review land without a session id cannot silently widen delivery.
            // Aligns with the plan_final_response and plan_review_next_round
            // paths in PlanReviewCompletionService.
            //
            // Fail-safe integrity (#144 follow-up): when the persisted payload
            // was missing or malformed we cannot produce a coherent
            // kind+feedback pair for this broadcast, so we skip the resolution
            // broadcast entirely. The row is already durably terminal and the
            // completion kickoff below still runs, so the plan surface
            // ultimately settles through `plan_final_response` — no user-
            // visible outcome is lost.
            //
            // Decision durability (#144 follow-up): the completion kickoff
            // runs regardless of hub-send outcome or request cancellation.
            // We use `CancellationToken.None` on the SendAsync so a client
            // disconnect on `ct` cannot strand the row terminal without ever
            // driving the resume path, and any hub-send exception is logged
            // and swallowed (the row is already terminal). Notification is
            // never a gate on execution.
            ILogger endpointLogger = loggerFactory.CreateLogger("PlanReviewEndpoints");
            string? reviewSessionId = row.Context.SessionId;
            if (!payloadParsed)
            {
                endpointLogger.LogWarning(
                    "plan_review_resolved suppressed for plan {PlanId} request {RequestId}: persisted response payload was missing or malformed; skipping broadcast to avoid advertising a losing caller's feedback.",
                    planId, requestId);
            }
            else if (string.IsNullOrWhiteSpace(reviewSessionId))
            {
                endpointLogger.LogWarning(
                    "plan_review_resolved suppressed for plan {PlanId} request {RequestId}: session identity missing; refusing to broadcast to Clients.All.",
                    planId, requestId);
            }
            else
            {
                try
                {
                    await hubContext.Clients.Group(reviewSessionId).SendAsync(
                        "plan_review_resolved", responseDto, CancellationToken.None);
                }
                catch (Exception hubEx)
                {
                    // Notification failure never gates the durable decision. Log
                    // (including OperationCanceledException) and let completion
                    // kickoff proceed so the plan settles.
                    endpointLogger.LogWarning(hubEx,
                        "plan_review_resolved SignalR send failed for plan {PlanId} request {RequestId}; decision remains durable and completion will still be driven.",
                        planId, requestId);
                }
            }

            // Drive the plan through the resume path so the reviewer's
            // decision produces a real final response (execute the effective
            // plan, filter, persist, broadcast). ResolveAsync is idempotent
            // and short-circuits when the plan is already terminal, and its
            // TryTransitionStatusAsync guard collapses two concurrent
            // KickoffCompletion calls to a single execution. This dispatch
            // is independent of the request cancellation token so a client
            // disconnect never leaves the durable row terminal without an
            // executed final response.
            if (completion is not null)
            {
                _ = KickoffCompletionAsync(completion, planId, subject);
            }

            return Results.Ok(responseDto);
        })
        .WithName("DecidePlanReview")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("moderate");

        // POST /api/plans/{planId}/clarifications/{requestId}/answer — reviewer answers a clarification.
        app.MapPost("/api/plans/{planId}/clarifications/{requestId}/answer", async (
            string planId,
            string requestId,
            [FromBody] PlanClarificationAnswerRequest body,
            IApprovalGate gate,
            IPlanStore planStore,
            HttpContext http,
            [FromServices] PlanReviewCompletionService? completion,
            [FromServices] GuardrailsMiddleware guardrails,
            CancellationToken ct) =>
        {
            if (RefuseAnonymous(http, out IResult? refusal))
                return refusal;
            if (body is null || string.IsNullOrWhiteSpace(body.Answer))
                return Results.BadRequest(new { error = "Answer is required." });

            string subject = UserIdentity.Resolve(http.User);
            if (!await OwnsPlanAsync(planStore, subject, planId, ct))
                return NotFound(planId);

            ApprovalRequest? row = await FindRowAsync(gate, subject, requestId, ct);
            if (row is null ||
                !string.Equals(row.Context.UserId, subject, StringComparison.Ordinal) ||
                !string.Equals(row.Context.PlanId, planId, StringComparison.Ordinal) ||
                !string.Equals(row.Context.Kind, ApprovalKind.Clarification, StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = "Clarification not found." });
            }

            // Guardrail-scan the clarification answer (#136): the reviewer's
            // Answer is substituted as the paused step's transcript and then
            // flows verbatim into the AccumulatedResults of every downstream
            // specialist call during resume. That is user-authored text
            // reaching an agent, so it MUST share the same
            // GuardrailsMiddleware.CheckInputAsync seam as /api/chat, the
            // edit path, and the reject-feedback path. On block, refuse the
            // answer without ever calling RespondAsync so the clarification
            // row stays Pending — no specialist or completion call runs
            // against a blocked answer.
            var answerProbe = new ChatRequest(
                Message: body.Answer,
                SessionId: row.Context.SessionId,
                User: new UserContext(subject, subject, string.Empty));
            GuardrailResult answerGuardrail = await guardrails.CheckInputAsync(answerProbe, ct);
            if (answerGuardrail.IsBlocked)
            {
                return Results.BadRequest(new
                {
                    error = "Clarification answer was blocked by input guardrails.",
                    code = "plan_clarification_answer_blocked",
                    refusal = answerGuardrail.RefusalMessage,
                });
            }

            var answer = new PlanClarificationAnswer { Answer = body.Answer };
            string payloadJson = JsonSerializer.Serialize(answer, _jsonOptions);
            ApprovalResult result = await gate.RespondAsync(
                requestId,
                ApprovalDecision.Approved,
                comment: body.Answer,
                responsePayload: payloadJson,
                ct: ct);

            if (completion is not null)
            {
                _ = KickoffCompletionAsync(completion, planId, subject);
            }

            return Results.Ok(new
            {
                requestId = result.RequestId,
                planId,
                decision = result.Decision.ToString().ToLowerInvariant(),
                respondedAt = result.RespondedAt,
                terminalReason = result.TerminalReason,
            });
        })
        .WithName("AnswerPlanClarification")
        .RequireAuthorization()
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .RequireRateLimiting("moderate");

        return app;
    }

    /// <summary>
    /// Fire-and-forget resume driver used from the decision / answer endpoints.
    /// Swallowed exceptions are logged inside the completion service — a
    /// failure MUST NOT surface as a 5xx to the reviewer because the
    /// authoritative row is already terminal at this point.
    /// </summary>
    private static async Task KickoffCompletionAsync(
        PlanReviewCompletionService completion, string planId, string subject)
    {
        try
        {
            await completion.ResolveAsync(planId, subject);
        }
        catch
        {
            // Errors are captured by the completion service's own logger; the
            // restart recovery service will pick up the plan on next boot.
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static (ApprovalDecision decision, string kind, IReadOnlyList<PlanReviewStepDto>? editedSteps, string? feedback, string? invalid)
        ClassifyDecision(PlanReviewDecisionRequest body)
    {
        string kind = body.Kind.Trim().ToLowerInvariant();
        switch (kind)
        {
            case PlanReviewKinds.Approve:
                return (ApprovalDecision.Approved, PlanReviewKinds.Approve, null, null, null);
            case PlanReviewKinds.Reject:
                if (string.IsNullOrWhiteSpace(body.Feedback))
                    return (default, kind, null, null, "Reject decisions must include feedback.");
                return (ApprovalDecision.Rejected, PlanReviewKinds.Reject, null, body.Feedback, null);
            case PlanReviewKinds.Edit:
                if (body.EditedSteps is null)
                    return (default, kind, null, null, "Edit decisions must include editedSteps.");
                return (ApprovalDecision.Modified, PlanReviewKinds.Edit, body.EditedSteps, null, null);
            default:
                return (default, kind, null, null, $"Unknown decision kind '{kind}'. Expected approve, reject, or edit.");
        }
    }

    /// <summary>
    /// Resolve the winning decision's <c>kind</c> and reviewer feedback from
    /// the persisted <see cref="ApprovalResult"/> so a losing concurrent
    /// caller does not advertise a decision the row does not record.
    /// <para>
    /// Integrity contract:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>Kind</c> is derived from
    ///     <see cref="ApprovalResult.Decision"/> (Approved/Rejected/Modified/
    ///     TimedOut/Orphaned). This is the durable row's authoritative outcome
    ///     — never the losing caller's requested kind. Timed-out and orphaned
    ///     rows normalize to <see cref="PlanReviewKinds.Reject"/> so the
    ///     resume path treats them as a terminal reject.</description></item>
    ///   <item><description>When <see cref="ApprovalResult.ResponsePayload"/>
    ///     is present and parses cleanly, <c>Feedback</c> is taken from the
    ///     persisted payload (the winner's actual text). If the persisted
    ///     payload carries a valid <c>Kind</c>, we use that too so the
    ///     approve/reject/edit granularity matches the persisted winner.</description></item>
    ///   <item><description>When the payload is missing or malformed we
    ///     omit feedback entirely (<c>null</c>) and return
    ///     <c>PayloadParsed = false</c>. The caller MUST skip any user-
    ///     visible resolution broadcast in that state so a losing caller
    ///     cannot smuggle its own feedback string into the SignalR event.
    ///     The decision itself remains durably recorded on the row.</description></item>
    /// </list>
    /// </summary>
    internal static (string Kind, string? Feedback, bool PayloadParsed) ExtractWinner(
        ApprovalResult result)
    {
        string derivedKind = KindFromDecision(result.Decision);

        if (string.IsNullOrWhiteSpace(result.ResponsePayload))
        {
            return (derivedKind, null, false);
        }

        PlanReviewResponsePayload? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PlanReviewResponsePayload>(
                result.ResponsePayload, _jsonOptions);
        }
        catch (JsonException)
        {
            return (derivedKind, null, false);
        }

        if (persisted is null)
        {
            return (derivedKind, null, false);
        }

        // Prefer the persisted payload's kind when it is well-formed and
        // consistent with the row's Decision family; otherwise fall back to
        // the decision-derived kind. Feedback comes from the persisted
        // payload only — this is the durable winner's actual text.
        string payloadKind = string.IsNullOrWhiteSpace(persisted.Kind)
            ? derivedKind
            : persisted.Kind.Trim().ToLowerInvariant();

        return (payloadKind, persisted.Feedback, true);
    }

    private static string KindFromDecision(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Approved => PlanReviewKinds.Approve,
        ApprovalDecision.Modified => PlanReviewKinds.Edit,
        ApprovalDecision.Rejected => PlanReviewKinds.Reject,
        ApprovalDecision.TimedOut => PlanReviewKinds.Reject,
        ApprovalDecision.Orphaned => PlanReviewKinds.Reject,
        ApprovalDecision.Pending => PlanReviewKinds.Reject,
        // Timed-out and orphaned rows are terminal-reject on the plan-review
        // surface (see PlanReviewCoordinator's derivation). Pending never
        // reaches ExtractWinner because RespondAsync only returns a settled
        // result, but if a caller ever passes one, treat it as reject so no
        // approve-shaped broadcast can slip through.
        _ => PlanReviewKinds.Reject,
    };

    private static async Task<ApprovalRequest?> FindRowAsync(
        IApprovalGate gate, string subject, string requestId, CancellationToken ct)
    {
        // The gate exposes GetPendingAsync (per user) and GetHistoryAsync; search
        // both so we can look up rows regardless of whether they're still open.
        IReadOnlyList<ApprovalRequest> pending = await gate.GetPendingAsync(subject, ct);
        ApprovalRequest? hit = pending.FirstOrDefault(r => r.RequestId == requestId);
        if (hit is not null) return hit;

        IReadOnlyList<ApprovalRequest> history = await gate.GetHistoryAsync(200, ct);
        return history.FirstOrDefault(r => r.RequestId == requestId
            && string.Equals(r.Context.UserId, subject, StringComparison.Ordinal));
    }

    private static async Task<bool> OwnsPlanAsync(
        IPlanStore store, string subject, string planId, CancellationToken ct)
    {
        Contracts.Persistence.PlanDetailDto? detail = await store.GetPlanAsync(subject, planId, ct);
        return detail is not null;
    }

    private static IResult NotFound(string planId) =>
        Results.NotFound(new { error = $"Plan '{planId}' not found." });

    private static bool RefuseAnonymous(HttpContext http, out IResult? refusal)
    {
        if (AnonymousCapabilityPolicy.IsAnonymousPrincipal(http.User))
        {
            refusal = Results.Json(
                new { error = "Anonymous callers do not own plan reviews.", code = "plan_review_unavailable" },
                statusCode: StatusCodes.Status403Forbidden);
            return true;
        }
        refusal = null;
        return false;
    }

    private static object ToListDto(ApprovalRequest r) => new
    {
        requestId = r.RequestId,
        planId = r.Context.PlanId,
        round = r.Context.RoundNumber,
        subject = r.Context.UserId,
        action = r.Context.Action,
        impact = r.Context.Impact,
        urgency = r.Context.Urgency,
        reasoning = r.Context.Reasoning,
        createdAt = r.CreatedAt,
        expiresAt = r.ExpiresAt,
        status = r.Decision.ToString().ToLowerInvariant(),
        payload = r.Context.Payload,
    };
}

/// <summary>Request body for POST /api/plans/{planId}/reviews/{requestId}/decision.</summary>
public sealed record PlanReviewDecisionRequest
{
    public required string Kind { get; init; }
    public string? Comment { get; init; }
    public string? Feedback { get; init; }
    public IReadOnlyList<PlanReviewStepDto>? EditedSteps { get; init; }
}

/// <summary>Request body for the clarification-answer endpoint.</summary>
public sealed record PlanClarificationAnswerRequest
{
    public required string Answer { get; init; }
}
