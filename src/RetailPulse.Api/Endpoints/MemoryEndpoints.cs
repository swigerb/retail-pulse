using RetailPulse.Api.Auth;
using RetailPulse.Contracts.Memory;

namespace RetailPulse.Api.Endpoints;

/// <summary>
/// Exposes conversation memory entries via REST for the telemetry panel.
/// GET /api/memory — list all memories for the authenticated user.
/// DELETE /api/memory/{id} — remove a single memory entry.
/// </summary>
public static class MemoryEndpoints
{
    public static WebApplication MapMemoryEndpoints(this WebApplication app)
    {
        // Conversation memory is per-user, protected data — every route in this group
        // requires the same authenticated user + app role + API scope policy as the rest
        // of the API. Without this the group would fall through to anonymous (the app sets
        // a DefaultPolicy, not a FallbackPolicy), so the group-level RequireAuthorization
        // is the security boundary for these endpoints.
        RouteGroupBuilder group = app.MapGroup("/api/memory")
            .WithTags("Memory")
            .RequireAuthorization();

        group.MapGet("/", async (IConversationMemory memory, HttpContext httpContext, CancellationToken ct) =>
        {
            string userId = UserIdentity.Resolve(httpContext.User);

            IReadOnlyList<MemoryEntry> entries = await memory.RecallAsync(userId, query: null, maxResults: 100, ct);

            var result = entries.Select(e => new
            {
                id = e.Id,
                content = e.Content,
                storedAt = e.CreatedAt.ToString("o"),
                expiresAt = e.ExpiresAt.ToString("o"),
                type = e.Type switch
                {
                    MemoryType.ConversationSummary => "conversation",
                    MemoryType.UserPreference => "preference",
                    MemoryType.EntityMention => "entity",
                    _ => "conversation"
                }
            });

            return Results.Ok(result);
        })
        .WithName("ListMemories")
        .WithSummary("List conversation memory entries for the current user");

        group.MapDelete("/{id}", async (string id, IConversationMemory memory, HttpContext httpContext, CancellationToken ct) =>
        {
            string userId = UserIdentity.Resolve(httpContext.User);

            await memory.ForgetEntryAsync(userId, id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteMemory")
        .WithSummary("Delete a single memory entry by ID");

        group.MapDelete("/", async (IConversationMemory memory, HttpContext httpContext, CancellationToken ct) =>
        {
            string userId = UserIdentity.Resolve(httpContext.User);

            await memory.ForgetAsync(userId, ct);
            return Results.NoContent();
        })
        .WithName("DeleteAllMemories")
        .WithSummary("Delete all memory entries for the current user");

        return app;
    }
}
