using RetailPulse.Api.Explainability;
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
            IReadOnlyList<TraceSummary> traces = traceCollector.GetRecentTraces(count ?? 20);
            return Results.Ok(traces);
        })
        .WithName("GetRecentTraces")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/traces/{traceId}/summary", (string traceId, ITraceCollector traceCollector) =>
        {
            StructuredTraceSummary? summary = traceCollector.GetStructuredSummary(traceId);
            return summary is not null
                ? Results.Ok(summary)
                : Results.NotFound(new { error = $"Trace '{traceId}' not found." });
        })
        .WithName("GetTraceSummary")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/traces/{traceId}/spans", (string traceId, ITraceCollector traceCollector) =>
        {
            IReadOnlyList<TraceSpan>? spans = traceCollector.GetSpans(traceId);
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
            string periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
            CostPeriod period = Enum.TryParse(periodStr, true, out CostPeriod p) ? p : CostPeriod.Week;
            CostSummary summary = await costTracker.GetSummaryAsync(period, ct);
            return Results.Ok(summary);
        })
        .WithName("GetCostSummary").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/costs/agents", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
        {
            string periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
            CostPeriod period = Enum.TryParse(periodStr, true, out CostPeriod p) ? p : CostPeriod.Week;
            IReadOnlyList<AgentCostBreakdown> agents = await costTracker.GetByAgentAsync(period, ct);
            return Results.Ok(agents);
        })
        .WithName("GetCostsByAgent").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/costs/tools", (HttpContext http, ITraceCollector traceCollector) =>
        {
            string periodStr = http.Request.Query["period"].FirstOrDefault() ?? "week";
            DateTimeOffset cutoff = GetCostPeriodCutoff(periodStr, DateTimeOffset.UtcNow);
            IReadOnlyList<ToolUsageStat> stats = traceCollector.GetToolStats(cutoff);
            return Results.Ok(stats);
        })
        .WithName("GetToolStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/costs/trend", async (HttpContext http, ICostTracker costTracker, CancellationToken ct) =>
        {
            string? daysStr = http.Request.Query["days"].FirstOrDefault();
            int days = int.TryParse(daysStr, out int d) ? d : 7;
            CostTrend trend = await costTracker.GetTrendAsync(days, ct);
            return Results.Ok(trend);
        })
        .WithName("GetCostTrend").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/audit", async (HttpContext http, IAuditLog auditLog, CancellationToken ct) =>
        {
            var query = new AuditQuery(
                AgentId: http.Request.Query["agentId"].FirstOrDefault(),
                UserId: http.Request.Query["userId"].FirstOrDefault(),
                From: DateTime.TryParse(http.Request.Query["from"].FirstOrDefault(), out DateTime from) ? from : null,
                To: DateTime.TryParse(http.Request.Query["to"].FirstOrDefault(), out DateTime to) ? to : null,
                Action: http.Request.Query["action"].FirstOrDefault(),
                Limit: int.TryParse(http.Request.Query["limit"].FirstOrDefault(), out int limit) ? limit : 50
            );

            IReadOnlyList<AuditEntry> entries = await auditLog.QueryAsync(query, ct);
            IEnumerable<ObservabilityContracts.AuditEntryDto> dto = entries.Select(e =>
                new ObservabilityContracts.AuditEntryDto(
                    e.Id, e.Timestamp, e.UserId, e.AgentId, e.Action,
                    e.InputSummary, e.OutputSummary, e.TokensUsed, e.Duration.TotalMilliseconds));
            return Results.Ok(dto);
        })
        .WithName("GetAuditLog").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/audit/stats", async (IAuditLog auditLog, CancellationToken ct) =>
        {
            AuditStats stats = await auditLog.GetStatsAsync(ct);
            return Results.Ok(stats);
        })
        .WithName("GetAuditStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/export/sessions", async (IConversationExport exporter, CancellationToken ct) =>
        {
            IReadOnlyList<ExportableSession> sessions = await exporter.ListSessionsAsync(ct);
            IEnumerable<ObservabilityContracts.ExportSessionDto> dto = sessions.Select(s =>
                new ObservabilityContracts.ExportSessionDto(
                    s.SessionId, s.StartedAt, s.MessageCount, s.AgentsUsed, s.TotalTokens));
            return Results.Ok(dto);
        })
        .WithName("ListExportSessions").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/observability/export/{sessionId}/preview", async (string sessionId, HttpContext http, IConversationExport exporter, CancellationToken ct) =>
        {
            int maxMessages = int.TryParse(http.Request.Query["max"].FirstOrDefault(), out int m) && m > 0 ? m : 20;
            SessionPreview? preview = await exporter.GetPreviewAsync(sessionId, maxMessages, ct);
            if (preview is null)
                return Results.NotFound(new { error = $"Session '{sessionId}' not found." });

            var dto = new ObservabilityContracts.ExportPreviewDto(
                preview.SessionId,
                [.. preview.Messages.Select(msg => new ObservabilityContracts.PreviewMessageDto(msg.Role, msg.Content, msg.Timestamp))],
                preview.TotalMessages);
            return Results.Ok(dto);
        })
        .WithName("PreviewExportSession").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/observability/export/{sessionId}", async (string sessionId, HttpContext http, ObservabilityContracts.ExportRequest? body, IConversationExport exporter, CancellationToken ct) =>
        {
            // Format may arrive in the JSON body ({ "format": "json" }) or the query string.
            string? formatStr = body?.Format ?? http.Request.Query["format"].FirstOrDefault();
            ExportFormat format = string.Equals(formatStr, "json", StringComparison.OrdinalIgnoreCase)
                ? ExportFormat.Json
                : ExportFormat.Markdown;

            try
            {
                ExportResult result = await exporter.ExportAsync(sessionId, format, ct);
                string contentType = format == ExportFormat.Json
                    ? "application/json"
                    : "text/markdown";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(result.Content);
                // Results.File sets Content-Type and Content-Disposition: attachment; filename=...
                return Results.File(bytes, $"{contentType}; charset=utf-8", result.FileName);
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
            CacheStats stats = await cache.GetStatsAsync(ct);
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
        app.MapGet("/api/explain/{traceId}", (string traceId, ExplainabilityService explainability) =>
        {
            ExplainabilityService.ExplanationTrace? trace = explainability.GetTrace(traceId);
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

        app.MapGet("/api/explain/session/{sessionId}", (string sessionId, ExplainabilityService explainability) =>
        {
            IReadOnlyList<ExplainabilityService.ExplanationTrace> traces = explainability.GetSessionTraces(sessionId);
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

    internal static DateTimeOffset GetCostPeriodCutoff(string? period, DateTimeOffset nowUtc)
    {
        DateTimeOffset utcNow = nowUtc.ToUniversalTime();

        return period?.ToLowerInvariant() switch
        {
            "today" => new DateTimeOffset(utcNow.Date, TimeSpan.Zero),
            "week" => utcNow.AddDays(-7),
            "month" => utcNow.AddDays(-30),
            "all" => DateTimeOffset.MinValue,
            _ => utcNow.AddDays(-7)
        };
    }
}
