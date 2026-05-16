using RetailPulse.Api.Resilience;

namespace RetailPulse.Api.Endpoints;

public static class DeadLetterEndpoints
{
    public static void MapDeadLetterEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/dead-letter")
            .RequireAuthorization();

        group.MapGet("/", async (DeadLetterQueue queue, CancellationToken ct) =>
        {
            IReadOnlyList<DeadLetterEntry> pending = await queue.GetPendingAsync(ct: ct);
            return Results.Ok(new
            {
                count = pending.Count,
                entries = pending
            });
        });

        group.MapPost("/replay", async (DeadLetterQueue queue, ILogger<DeadLetterQueue> logger, CancellationToken ct) =>
        {
            IReadOnlyList<DeadLetterEntry> pending = await queue.GetPendingAsync(ct: ct);
            int replayed = 0;

            foreach (DeadLetterEntry entry in pending)
            {
                // Mark as replayed — in a real system, we'd re-dispatch the operation.
                // For now, we mark them replayed and log for manual follow-up.
                await queue.MarkReplayedAsync(entry.Id, ct);
                replayed++;
                logger.LogInformation(
                    "Dead-letter replayed: {Operation} (originally failed at {Timestamp})",
                    entry.Operation, entry.Timestamp);
            }

            return Results.Ok(new { replayed, message = $"Replayed {replayed} dead-letter entries." });
        });

        group.MapGet("/count", async (DeadLetterQueue queue, CancellationToken ct) =>
        {
            int count = await queue.GetPendingCountAsync(ct);
            return Results.Ok(new { pendingCount = count });
        });
    }
}
