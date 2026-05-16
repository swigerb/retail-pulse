using RetailPulse.Api.Escalation;
using RetailPulse.Contracts.Routing;
using ChatRequest = RetailPulse.Contracts.ChatRequest;

namespace RetailPulse.Api.Endpoints;

public static class EscalationEndpoints
{
    public static WebApplication MapEscalationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/escalate", async (ChatRequest request, EscalationOrchestrator escalation, IAgentRouter router, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Field 'message' is required." });

            RoutingDecision decision = await router.RouteAsync(request.Message, request.History, request.User, null, ct);
            EscalationOrchestrator.EscalationResult result = await escalation.EscalateAsync(request, decision, ct);

            return Results.Ok(new
            {
                reply = result.Reply,
                level = result.Level,
                agentsConsulted = result.AgentsConsulted,
                durationMs = result.DurationMs,
                needsHumanReview = result.NeedsHumanReview,
                escalationReason = result.EscalationReason
            });
        })
        .WithName("Escalate").RequireAuthorization().RequireRateLimiting("strict");

        return app;
    }
}
