using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Endpoints;

public static class PromoEndpoints
{
    public static WebApplication MapPromoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/promo/calendar", async (HttpContext http, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var brand = http.Request.Query["brand"].FirstOrDefault();
            var region = http.Request.Query["region"].FirstOrDefault();
            var monthsStr = http.Request.Query["months"].FirstOrDefault();
            var months = int.TryParse(monthsStr, out var m) ? m : 6;

            var client = httpFactory.CreateClient("McpServer");
            var url = $"/api/promo/calendar?months={months}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetPromoCalendar").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/promo/types", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var client = httpFactory.CreateClient("McpServer");
            var response = await client.GetAsync("/api/promo/types", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetPromoTypes").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapPost("/api/taskmodule/promo", async (PromoEvaluationRequest request, IHttpClientFactory httpFactory, IApprovalGate approvalGate, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Brand) || string.IsNullOrWhiteSpace(request.Region) ||
                string.IsNullOrWhiteSpace(request.PromoType) || request.Budget <= 0)
            {
                return Results.BadRequest(new { error = "Fields brand, region, promoType, and budget (> 0) are required." });
            }

            if (!DateOnly.TryParse(request.StartDate, out var startDate) || !DateOnly.TryParse(request.EndDate, out var endDate))
            {
                return Results.BadRequest(new { error = "startDate and endDate must be valid ISO dates (yyyy-MM-dd)." });
            }

            var durationWeeks = Math.Max(1, (endDate.DayNumber - startDate.DayNumber) / 7);
            var client = httpFactory.CreateClient("McpServer");

            // Orchestrate: call all promo tools in parallel
            var historyTask = client.GetStringAsync(
                $"/api/promo/history?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&months=12", ct);
            var liftTask = client.GetStringAsync(
                $"/api/promo/calculate-lift?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}", ct);
            var timingTask = client.GetStringAsync(
                $"/api/promo/evaluate-timing?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&startDate={Uri.EscapeDataString(request.StartDate)}&endDate={Uri.EscapeDataString(request.EndDate)}", ct);
            var roiTask = client.GetStringAsync(
                $"/api/promo/estimate-roi?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}&durationWeeks={durationWeeks}", ct);

            await Task.WhenAll(historyTask, liftTask, timingTask, roiTask);

            var historyJson = await historyTask;
            var liftJson = await liftTask;
            var timingJson = await timingTask;
            var roiJson = await roiTask;

            // Parse ROI for approval gate decision
            using var roiDoc = System.Text.Json.JsonDocument.Parse(roiJson);
            var expectedRoi = roiDoc.RootElement.TryGetProperty("expected_roi", out var roiProp) ? roiProp.GetDouble() : 0;

            // Determine recommendation
            var recommendation = expectedRoi switch
            {
                >= 3.0 => "strongly_recommended",
                >= 2.0 => "recommended",
                >= 1.0 => "proceed_with_caution",
                _ => "not_recommended"
            };

            // Build risk factors
            var riskFactors = new List<string>();
            using var timingDoc = System.Text.Json.JsonDocument.Parse(timingJson);
            if (timingDoc.RootElement.TryGetProperty("conflicts", out var conflicts) && conflicts.GetArrayLength() > 0)
                riskFactors.Add($"{conflicts.GetArrayLength()} overlapping campaign(s) detected");
            if (timingDoc.RootElement.TryGetProperty("risks", out var risks))
            {
                foreach (var risk in risks.EnumerateArray())
                {
                    if (risk.TryGetProperty("detail", out var detail))
                        riskFactors.Add(detail.GetString() ?? "Unknown risk");
                }
            }
            if (expectedRoi < 1.0)
                riskFactors.Add("Expected ROI below breakeven (1.0x)");
            if (request.Budget > 500000)
                riskFactors.Add("High-budget campaign (>$500K) — requires executive approval");

            // Check approval gate trigger
            string? approvalRequestId = null;
            var requiresApproval = request.Budget > 500000 || (expectedRoi < 2.0 && request.Budget > 100000);
            if (requiresApproval)
            {
                var reason = request.Budget > 500000
                    ? $"High-budget promo: ${request.Budget:N0} for {request.Brand} in {request.Region}"
                    : $"Low-ROI risk: {expectedRoi:F2}x ROI with ${request.Budget:N0} budget for {request.Brand}";

                var approvalRequest = await approvalGate.RequestApprovalAsync(new ApprovalContext(
                    AgentId: "promo-planning",
                    UserId: "taskmodule",
                    Action: $"Execute {request.PromoType} promotion for {request.Brand} in {request.Region}",
                    Impact: $"Budget: ${request.Budget:N0}, Expected ROI: {expectedRoi:F2}x, Duration: {durationWeeks} weeks",
                    Urgency: request.Budget > 500000 ? "high" : "medium",
                    Reasoning: reason
                ), ct);

                approvalRequestId = approvalRequest.RequestId;
                logger.LogInformation("Promo task module triggered approval gate: {RequestId} for {Brand}/{Region}", approvalRequestId, request.Brand, request.Region);
            }

            return Results.Ok(new
            {
                recommendation,
                brand = request.Brand,
                region = request.Region,
                promo_type = request.PromoType,
                budget = request.Budget,
                period = new { start = request.StartDate, end = request.EndDate, duration_weeks = durationWeeks },
                target_lift = request.TargetLift,
                roi_estimate = System.Text.Json.JsonSerializer.Deserialize<object>(roiJson),
                timing_assessment = System.Text.Json.JsonSerializer.Deserialize<object>(timingJson),
                lift_analysis = System.Text.Json.JsonSerializer.Deserialize<object>(liftJson),
                historical_context = System.Text.Json.JsonSerializer.Deserialize<object>(historyJson),
                risk_factors = riskFactors,
                approval = requiresApproval ? new
                {
                    required = true,
                    request_id = approvalRequestId,
                    reason = request.Budget > 500000 ? "high_budget" : "low_roi_risk"
                } : new
                {
                    required = false,
                    request_id = (string?)null,
                    reason = string.Empty
                }
            });
        })
        .WithName("PromoTaskModule").RequireAuthorization().RequireRateLimiting("moderate");

        return app;
    }
}

record PromoEvaluationRequest(
    string Brand,
    string Region,
    string PromoType,
    double Budget,
    string StartDate,
    string EndDate,
    double? TargetLift = null
);
