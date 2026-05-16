using RetailPulse.Api.Caching;

namespace RetailPulse.Api.Endpoints;

public static class CacheEndpoints
{
    public static void MapCacheEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/cache")
            .WithTags("Cache Admin")
            .RequireAuthorization();

        group.MapPost("/invalidate", (string? tool, ToolResultCache cache) =>
        {
            cache.Invalidate(tool);
            return Results.NoContent();
        })
        .WithName("InvalidateToolCache")
        .WithDescription("Invalidates cached tool results. If 'tool' query param is provided, only that tool's cache is cleared.");

        group.MapGet("/stats", (ToolResultCache cache) => Results.Ok(new
        {
            hits = cache.Hits,
            misses = cache.Misses,
            hitRate = (cache.Hits + cache.Misses) > 0
                    ? (double)cache.Hits / (cache.Hits + cache.Misses)
                    : 0.0
        }))
        .WithName("GetToolCacheStats")
        .WithDescription("Returns tool result cache hit/miss statistics.");
    }
}
