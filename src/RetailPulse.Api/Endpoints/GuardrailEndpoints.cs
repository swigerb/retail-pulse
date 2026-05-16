using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Endpoints;

public static class GuardrailEndpoints
{
    public static WebApplication MapGuardrailEndpoints(this WebApplication app)
    {
        app.MapGet("/api/guardrails/log", async (ISuspiciousRequestLog log, HttpContext http, CancellationToken ct) =>
        {
            string? countStr = http.Request.Query["count"].FirstOrDefault();
            int count = int.TryParse(countStr, out int c) ? c : 50;

            IReadOnlyList<SuspiciousRequest> recent = await log.GetRecentAsync(count, ct);
            return Results.Ok(recent.Select(r => new
            {
                id = r.Id,
                timestamp = r.Timestamp,
                requestText = r.RequestText,
                detectionType = r.DetectionType,
                userContext = r.UserContext,
                action = r.Action
            }));
        })
        .WithName("GetGuardrailsLog").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/guardrails/stats", async (ISuspiciousRequestLog log, CancellationToken ct) =>
        {
            GuardrailsStats stats = await log.GetStatsAsync(ct);
            return Results.Ok(new
            {
                totalBlocked = stats.TotalBlocked,
                jailbreakAttempts = stats.JailbreakAttempts,
                piiDetections = stats.PiiDetections,
                accessDenials = stats.AccessDenials,
                since = stats.Since
            });
        })
        .WithName("GetGuardrailsStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/guardrails/config", (GuardrailsConfig config) => Results.Ok(new
        {
            piiDetectionEnabled = config.PiiDetectionEnabled,
            jailbreakDetectionEnabled = config.JailbreakDetectionEnabled,
            autoRedactPii = config.AutoRedactPii,
            maxInputLength = config.MaxInputLength,
            piiPatterns = Guardrails.GuardrailPatterns.PiiPatterns.Select(p => p.Name).ToList(),
            jailbreakPatterns = Guardrails.GuardrailPatterns.JailbreakPatterns.Select(p => p.Name).ToList()
        }))
        .WithName("GetGuardrailsConfig").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPut("/api/guardrails/config", (GuardrailsConfigUpdateDto body, GuardrailsConfig config) =>
        {
            if (body.PiiDetectionEnabled.HasValue)
                config.PiiDetectionEnabled = body.PiiDetectionEnabled.Value;
            if (body.JailbreakDetectionEnabled.HasValue)
                config.JailbreakDetectionEnabled = body.JailbreakDetectionEnabled.Value;
            if (body.AutoRedactPii.HasValue)
                config.AutoRedactPii = body.AutoRedactPii.Value;
            if (body.MaxInputLength.HasValue)
                config.MaxInputLength = body.MaxInputLength.Value;

            return Results.Ok(new
            {
                piiDetectionEnabled = config.PiiDetectionEnabled,
                jailbreakDetectionEnabled = config.JailbreakDetectionEnabled,
                autoRedactPii = config.AutoRedactPii,
                maxInputLength = config.MaxInputLength,
                status = "updated"
            });
        })
        .WithName("UpdateGuardrailsConfig").RequireAuthorization().RequireRateLimiting("moderate");

        return app;
    }
}

record GuardrailsConfigUpdateDto(
    bool? PiiDetectionEnabled = null,
    bool? JailbreakDetectionEnabled = null,
    bool? AutoRedactPii = null,
    int? MaxInputLength = null);
