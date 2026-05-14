using RetailPulse.Contracts.Alerts;

namespace RetailPulse.Api.Endpoints;

public static class AlertEndpoints
{
    public static WebApplication MapAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/api/alerts/active", async (IAlertService alertService, CancellationToken ct) =>
        {
            var alerts = await alertService.GetActiveAlertsAsync(ct);
            return Results.Ok(alerts);
        })
        .WithName("GetActiveAlerts")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapGet("/api/alerts/history", async (IAlertService alertService, HttpContext http, CancellationToken ct) =>
        {
            var userId = http.Request.Query["userId"].FirstOrDefault() ?? "default";
            var limitStr = http.Request.Query["limit"].FirstOrDefault();
            var limit = int.TryParse(limitStr, out var l) ? l : 50;

            var alerts = await alertService.GetHistoryAsync(userId, limit, ct);
            return Results.Ok(alerts);
        })
        .WithName("GetAlertHistory")
        .RequireAuthorization()
        .RequireRateLimiting("relaxed");

        app.MapPost("/api/alerts/{alertId}/snooze", async (string alertId, AlertSnoozeDto body, IAlertService alertService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.UserId))
                return Results.BadRequest(new { error = "userId is required." });

            var duration = body.DurationHours switch
            {
                <= 0 => TimeSpan.FromHours(1),
                _ => TimeSpan.FromHours(body.DurationHours)
            };

            await alertService.SnoozeAsync(body.AlertType ?? alertId, body.UserId, duration, ct);
            return Results.Ok(new { alertId, snoozedFor = duration.ToString(), userId = body.UserId });
        })
        .WithName("SnoozeAlert")
        .RequireAuthorization()
        .RequireRateLimiting("moderate");

        app.MapPost("/api/alerts/{alertId}/dismiss", async (string alertId, AlertDismissDto body, IAlertService alertService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.UserId))
                return Results.BadRequest(new { error = "userId is required." });

            await alertService.DismissAsync(alertId, body.UserId, ct);
            return Results.Ok(new { alertId, dismissed = true, userId = body.UserId });
        })
        .WithName("DismissAlert")
        .RequireAuthorization()
        .RequireRateLimiting("moderate");

        return app;
    }
}

record AlertSnoozeDto(string UserId, string? AlertType = null, double DurationHours = 1);
record AlertDismissDto(string UserId);
