using System.Text.Json;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Endpoints;

public static class PromoEndpoints
{
    public static WebApplication MapPromoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/promo/calendar", async (HttpContext http, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            string? brand = http.Request.Query["brand"].FirstOrDefault();
            string? region = http.Request.Query["region"].FirstOrDefault();
            string? monthsStr = http.Request.Query["months"].FirstOrDefault();
            int months = int.TryParse(monthsStr, out int m) ? m : 6;

            HttpClient client = httpFactory.CreateClient("McpServer");
            string url = $"/api/promo/calendar?months={months}";
            if (!string.IsNullOrWhiteSpace(brand)) url += $"&brand={Uri.EscapeDataString(brand)}";
            if (!string.IsNullOrWhiteSpace(region)) url += $"&region={Uri.EscapeDataString(region)}";

            HttpResponseMessage response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return Results.Content(json, "application/json");
        })
        .WithName("GetPromoCalendar").RequireAuthorization().RequireRateLimiting("relaxed");

        // Existing campaigns for the Campaign Planner panel.
        //
        // The SPA has always called GET /api/campaigns, which was never mapped,
        // it 404'd, promoApi.fetchExistingCampaigns threw on the non-OK status,
        // and the uncaught error took the whole dashboard down through the
        // app-level ErrorBoundary. The data it wants is the promo calendar, so
        // this projects that MCP payload into the PromoCampaign shape the panel
        // declares. An unreachable MCP server yields an empty list rather than a
        // 500: an empty planner is a better failure mode than a dead dashboard.
        app.MapGet("/api/campaigns", async (
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            try
            {
                HttpClient client = httpFactory.CreateClient("McpServer");
                HttpResponseMessage response = await client.GetAsync("/api/promo/calendar?months=12", ct);
                response.EnsureSuccessStatusCode();

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
                using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("calendar", out JsonElement calendar)
                    || calendar.ValueKind != JsonValueKind.Array)
                {
                    return Results.Ok(Array.Empty<object>());
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                var campaigns = new List<object>(calendar.GetArrayLength());
                int index = 0;

                foreach (JsonElement row in calendar.EnumerateArray())
                {
                    string start = ReadString(row, "start_date");
                    string end = ReadString(row, "end_date");

                    campaigns.Add(new
                    {
                        id = $"campaign-{index++}",
                        name = ReadString(row, "campaign"),
                        brand = ReadString(row, "brand"),
                        region = ReadString(row, "region"),
                        promoType = ReadString(row, "promo_type"),
                        budget = ReadDouble(row, "spend"),
                        startDate = start,
                        endDate = end,
                        roi = ReadDouble(row, "roi"),
                        status = DeriveStatus(start, end, now),
                    });
                }

                return Results.Ok(campaigns);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(typeof(PromoEndpoints))
                    .LogWarning(ex, "Campaign list unavailable from the MCP server.");
                return Results.Ok(Array.Empty<object>());
            }
        })
        .WithName("GetCampaigns").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/promo/types", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            HttpClient client = httpFactory.CreateClient("McpServer");
            HttpResponseMessage response = await client.GetAsync("/api/promo/types", ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
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

            if (!DateOnly.TryParse(request.StartDate, out DateOnly startDate) || !DateOnly.TryParse(request.EndDate, out DateOnly endDate))
            {
                return Results.BadRequest(new { error = "startDate and endDate must be valid ISO dates (yyyy-MM-dd)." });
            }

            if (endDate <= startDate)
            {
                return Results.BadRequest(new { error = "endDate must be after startDate." });
            }

            int durationWeeks = Math.Max(1, (int)Math.Ceiling((endDate.DayNumber - startDate.DayNumber) / 7.0));
            HttpClient client = httpFactory.CreateClient("McpServer");
            string roiUrl = $"/api/promo/estimate-roi?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}&durationWeeks={durationWeeks}";
            if (request.TargetLiftPercent is > 0)
                roiUrl += $"&targetLiftPercent={request.TargetLiftPercent.Value}";

            // Call all promo tools in parallel so the planner stays responsive.
            Task<string> historyTask = client.GetStringAsync(
                $"/api/promo/history?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&months=24", ct);
            Task<string> liftTask = client.GetStringAsync(
                $"/api/promo/calculate-lift?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&promoType={Uri.EscapeDataString(request.PromoType)}&spend={request.Budget}", ct);
            Task<string> timingTask = client.GetStringAsync(
                $"/api/promo/evaluate-timing?brand={Uri.EscapeDataString(request.Brand)}&region={Uri.EscapeDataString(request.Region)}&startDate={Uri.EscapeDataString(request.StartDate)}&endDate={Uri.EscapeDataString(request.EndDate)}", ct);
            Task<string> roiTask = client.GetStringAsync(roiUrl, ct);

            await Task.WhenAll(historyTask, liftTask, timingTask, roiTask);

            string historyJson = await historyTask;
            string liftJson = await liftTask;
            string timingJson = await timingTask;
            string roiJson = await roiTask;

            // Parse ROI for approval gate decision.
            using var roiDoc = JsonDocument.Parse(roiJson);
            if (TryReadError(roiDoc.RootElement, out string? roiError))
            {
                return Results.UnprocessableEntity(new { error = roiError });
            }

            double? expectedRoi = ReadExpectedRoi(roiDoc.RootElement);
            bool insufficientHistory = ReadBoolean(roiDoc.RootElement, "insufficient_history") || expectedRoi is null;

            // Determine recommendation
            string recommendation = insufficientHistory ? "insufficient_history" : expectedRoi!.Value switch
            {
                >= 3.0 => "strongly_recommended",
                >= 2.0 => "recommended",
                >= 0.95 => "proceed_with_caution",
                _ => "not_recommended"
            };

            // Build risk factors
            var riskFactors = new List<string>();
            using var timingDoc = JsonDocument.Parse(timingJson);
            if (timingDoc.RootElement.TryGetProperty("conflicts", out JsonElement conflicts) && conflicts.GetArrayLength() > 0)
                riskFactors.Add($"{conflicts.GetArrayLength()} overlapping campaign(s) detected");
            if (timingDoc.RootElement.TryGetProperty("risks", out JsonElement risks))
            {
                foreach (JsonElement risk in risks.EnumerateArray())
                {
                    if (risk.TryGetProperty("detail", out JsonElement detail))
                        riskFactors.Add(detail.GetString() ?? "Unknown risk");
                }
            }
            if (!insufficientHistory && expectedRoi!.Value < 0.95)
                riskFactors.Add("Expected ROI below breakeven (1.0x)");
            if (request.Budget > 500000)
                riskFactors.Add("High-budget campaign (>$500K) requires executive approval");

            // Check approval gate trigger
            string? approvalRequestId = null;
            bool requiresApproval = !insufficientHistory && (request.Budget > 500000 || (expectedRoi!.Value < 2.0 && request.Budget > 100000));
            if (requiresApproval)
            {
                string reason = request.Budget > 500000
                    ? $"High-budget promo: ${request.Budget:N0} for {request.Brand} in {request.Region}"
                    : $"Low-ROI risk: {expectedRoi!.Value:F2}x ROI with ${request.Budget:N0} budget for {request.Brand}";

                ApprovalRequest approvalRequest = await approvalGate.RequestApprovalAsync(new ApprovalContext(
                    AgentId: "promo-planning",
                    UserId: "taskmodule",
                    Action: $"Execute {request.PromoType} promotion for {request.Brand} in {request.Region}",
                    Impact: $"Budget: ${request.Budget:N0}, Expected ROI: {expectedRoi!.Value:F2}x, Duration: {durationWeeks} weeks",
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
                target_lift = request.TargetLiftPercent,
                roi_estimate = JsonSerializer.Deserialize<object>(roiJson),
                timing_assessment = JsonSerializer.Deserialize<object>(timingJson),
                lift_analysis = JsonSerializer.Deserialize<object>(liftJson),
                historical_context = JsonSerializer.Deserialize<object>(historyJson),
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

    private static string ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadDouble(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double parsed)
            ? parsed
            : 0d;

    private static bool TryReadError(JsonElement root, out string? error)
    {
        if (root.TryGetProperty("error", out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            error = value.GetString();
            return !string.IsNullOrWhiteSpace(error);
        }

        error = null;
        return false;
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private static double? ReadExpectedRoi(JsonElement root)
    {
        return root.TryGetProperty("roi", out JsonElement roi)
            && roi.ValueKind == JsonValueKind.Object
            && roi.TryGetProperty("expected", out JsonElement nestedExpected)
            && nestedExpected.TryGetDouble(out double nestedValue)
            ? nestedValue
            : root.TryGetProperty("expected_roi", out JsonElement flatExpected)
                && flatExpected.TryGetDouble(out double flatValue)
                ? flatValue
                : null;
    }

    /// <summary>
    /// The promo calendar carries dates but no lifecycle state, while the planner's
    /// PromoCampaign contract requires one. Derive it from the window so the panel can
    /// group campaigns honestly instead of labelling every row the same.
    /// </summary>
    private static string DeriveStatus(string startDate, string endDate, DateTimeOffset now)
    {
        bool hasStart = DateTimeOffset.TryParse(startDate, out DateTimeOffset start);
        bool hasEnd = DateTimeOffset.TryParse(endDate, out DateTimeOffset end);

        return hasEnd && end < now ? "completed"
            : hasStart && start > now ? "planned"
            : hasStart && hasEnd ? "active"
            : "proposed";
    }
}

record PromoEvaluationRequest(
    string Brand,
    string Region,
    string PromoType,
    double Budget,
    string StartDate,
    string EndDate,
    double? TargetLiftPercent = null
);
