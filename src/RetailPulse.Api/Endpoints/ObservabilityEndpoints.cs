using RetailPulse.Contracts.Caching;
using RetailPulse.Contracts.Observability;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Api.Endpoints;

public static class ObservabilityEndpoints
{
    public static WebApplication MapObservabilityEndpoints(this WebApplication app)
    {
        // ── Trace endpoints ──────────────────────────────────────────────────
        app.MapGet("/api/traces/recent", (ITraceCollector traceCollector, int? count) =>
        {
            var traces = traceCollector.GetRecentTraces(count ?? 20);
            return Results.Ok(traces);
        })
        .WithName("GetRecentTraces")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/traces/{traceId}/summary", (string traceId, ITraceCollector traceCollector) =>
        {
            var summary = traceCollector.GetStructuredSummary(traceId);
            return summary is not null
                ? Results.Ok(summary)
                : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
        })
        .WithName("GetTraceSummary")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/traces/{traceId}/spans", (string traceId, ITraceCollector traceCollector) =>
        {
            var spans = traceCollector.GetSpans(traceId);
            return spans is not null
                ? Results.Ok(spans)
                : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
        })
        .WithName("GetTraceSpans")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        // ── Observability endpoints ──────────────────────────────────────────
        app.MapGet("/api/observability/costs", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
        {
            var periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
            var period = Enum.TryParse<CostPeriod>(periodStr, true, out var p) ? p : CostPeriod.Week;
            var summary = await costTracker.GetSummaryAsync(period, ct);
            return Results.Ok(summary);
        })
        .WithName("GetCostSummary").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/costs/agents", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
        {
            var periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
            var period = Enum.TryParse<CostPeriod>(periodStr, true, out var p) ? p : CostPeriod.Week;
            var agents = await costTracker.GetByAgentAsync(period, ct);
            return Results.Ok(agents);
        })
        .WithName("GetCostsByAgent").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/costs/trend", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
        {
            var daysStr = http.Request.Query["days"].FirstOrDefault();
            var days = int.TryParse(daysStr, out var d) ? d : 7;
            var trend = await costTracker.GetTrendAsync(days, ct);
            return Results.Ok(trend);
        })
        .WithName("GetCostTrend").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/audit", async (HttpContext http, IAuditLog auditLog, CancellationToken ct) =>
        {
            var query = new AuditQuery(
                AgentId: http.Request.Query["agentId"].FirstOrDefault(),
                UserId: http.Request.Query["userId"].FirstOrDefault(),
                From: DateTime.TryParse(http.Request.Query["from"].FirstOrDefault(), out var from) ? from : null,
                To: DateTime.TryParse(http.Request.Query["to"].FirstOrDefault(), out var to) ? to : null,
                Action: http.Request.Query["action"].FirstOrDefault(),
                Limit: int.TryParse(http.Request.Query["limit"].FirstOrDefault(), out var limit) ? limit : 50
            );

            var entries = await auditLog.QueryAsync(query, ct);
            return Results.Ok(entries);
        })
        .WithName("GetAuditLog").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/audit/stats", async (IAuditLog auditLog, CancellationToken ct) =>
        {
            var stats = await auditLog.GetStatsAsync(ct);
            return Results.Ok(stats);
        })
        .WithName("GetAuditStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/export/sessions", async (IConversationExport exporter, CancellationToken ct) =>
        {
            var sessions = await exporter.ListSessionsAsync(ct);
            return Results.Ok(sessions);
        })
        .WithName("ListExportSessions").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/observability/export/{sessionId}", async (string sessionId, HttpContext http, IConversationExport exporter, CancellationToken ct) =>
        {
            var formatStr = http.Request.Query["format"].FirstOrDefault() ?? "markdown";
            var format = string.Equals(formatStr, "json", StringComparison.OrdinalIgnoreCase)
                ? ExportFormat.Json
                : ExportFormat.Markdown;

            try
            {
                var result = await exporter.ExportAsync(sessionId, format, ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });
            }
        })
        .WithName("ExportSession").RequireAuthorization().RequireRateLimiting("moderate");

        // ── Cache endpoints ──────────────────────────────────────────────────
        app.MapGet("/api/cache/stats", async (IResponseCache cache, CancellationToken ct) =>
        {
            var stats = await cache.GetStatsAsync(ct);
            return Results.Ok(new
            {
                totalEntries = stats.TotalEntries,
                hits = stats.Hits,
                misses = stats.Misses,
                hitRate = Math.Round(stats.HitRate, 4),
                memoryBytes = stats.MemoryBytes
            });
        })
        .WithName("GetCacheStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapDelete("/api/cache", async (IResponseCache cache, CancellationToken ct) =>
        {
            await cache.InvalidateAsync(null, ct);
            return Results.Ok(new { status = "cleared" });
        })
        .WithName("ClearCache").RequireAuthorization().RequireRateLimiting("moderate");

        app.MapDelete("/api/cache/{key}", async (string key, IResponseCache cache, CancellationToken ct) =>
        {
            await cache.InvalidateAsync(key, ct);
            return Results.Ok(new { key, status = "invalidated" });
        })
        .WithName("InvalidateCacheKey").RequireAuthorization().RequireRateLimiting("moderate");

        // ── Explainability endpoints ─────────────────────────────────────────
        app.MapGet("/api/explain/{traceId}", (string traceId, RetailPulse.Api.Explainability.ExplainabilityService explainability) =>
        {
            var trace = explainability.GetTrace(traceId);
            if (trace is null)
                return Results.NotFound(new { error = $"Trace '{traceId}' not found." });

            return Results.Ok(new
            {
                traceId,
                trace.SessionId,
                trace.Query,
                trace.ToolCallCount,
                trace.TotalDurationMs,
                trace.StartedAt,
                dataSources = trace.DataSources,
                reasoningChain = trace.ReasoningChain,
                explanation = explainability.BuildExplanation(traceId)
            });
        })
        .WithName("GetExplanation").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/explain/session/{sessionId}", (string sessionId, RetailPulse.Api.Explainability.ExplainabilityService explainability) =>
        {
            var traces = explainability.GetSessionTraces(sessionId);
            return Results.Ok(traces.Select(t => new
            {
                traceId = $"{t.SessionId}-{t.StartedAt:yyyyMMddHHmmss}",
                t.Query,
                t.ToolCallCount,
                t.TotalDurationMs,
                t.StartedAt
            }));
        })
        .WithName("GetSessionTraces").RequireAuthorization().RequireRateLimiting("relaxed");

        return app;
    }
}
