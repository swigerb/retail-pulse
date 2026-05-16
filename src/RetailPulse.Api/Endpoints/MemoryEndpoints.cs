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
        RouteGroupBuilder group = app.MapGroup("/api/memory").WithTags("Memory");

        group.MapGet("/", async (IConversationMemory memory, HttpContext httpContext, CancellationToken ct) =>
        {
            string userId = httpContext.User.FindFirst("oid")?.Value
                      ?? httpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                      ?? "anonymous";

            IReadOnlyList<MemoryEntry> entries = await memory.RecallAsync(userId, query: null, maxResults: 100, ct);

            var result = entries.Select(e => new
            {
                id = e.Id,
                content = e.Content,
                createdAt = e.CreatedAt.ToString("o"),
                type = e.Type switch
                {
                    MemoryType.ConversationSummary => "fact",
                    MemoryType.UserPreference => "preference",
                    MemoryType.EntityMention => "context",
                    _ => "fact"
                }
            });

            return Results.Ok(result);
        })
        .WithName("ListMemories")
        .WithSummary("List conversation memory entries for the current user");

        group.MapDelete("/{id}", async (string id, IConversationMemory memory, HttpContext httpContext, CancellationToken ct) =>
        {
            string userId = httpContext.User.FindFirst("oid")?.Value
                      ?? httpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                      ?? "anonymous";

            await memory.ForgetEntryAsync(userId, id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteMemory")
        .WithSummary("Delete a single memory entry by ID");

        return app;
    }
}
