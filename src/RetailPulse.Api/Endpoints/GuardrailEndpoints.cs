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
                action = r.Action,
                category = r.Category,
                severity = r.Severity,
                decision = r.Decision
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
                contentSafetyBlocks = stats.ContentSafetyBlocks,
                contentSafetyFlags = stats.ContentSafetyFlags,
                since = stats.Since
            });
        })
        .WithName("GetGuardrailsStats").RequireAuthorization().RequireRateLimiting("relaxed");

        app.MapGet("/api/guardrails/config", (GuardrailsConfig config) => Results.Ok(ProjectConfig(config)))
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

            // Content Safety runtime toggles — the endpoint URL and any
            // credentials are deliberately server-side only. Thresholds and
            // fail-policy are safe to mutate at runtime because the evaluator
            // reads the live GuardrailsConfig on every call.
            if (body.ContentSafety is { } cs)
            {
                if (cs.FailPolicy is not null && Enum.TryParse(cs.FailPolicy, ignoreCase: true, out ContentSafetyFailPolicy policy))
                    config.ContentSafety.OnUnavailable = policy;
                if (cs.HateThreshold.HasValue)
                    config.ContentSafety.Thresholds.Hate = cs.HateThreshold.Value;
                if (cs.SexualThreshold.HasValue)
                    config.ContentSafety.Thresholds.Sexual = cs.SexualThreshold.Value;
                if (cs.ViolenceThreshold.HasValue)
                    config.ContentSafety.Thresholds.Violence = cs.ViolenceThreshold.Value;
                if (cs.SelfHarmThreshold.HasValue)
                    config.ContentSafety.Thresholds.SelfHarm = cs.SelfHarmThreshold.Value;
            }

            return Results.Ok(ProjectConfig(config) with { Status = "updated" });
        })
        .WithName("UpdateGuardrailsConfig").RequireAuthorization().RequireRateLimiting("moderate");

        return app;
    }

    private static GuardrailsConfigResponse ProjectConfig(GuardrailsConfig config) => new(
        PiiDetectionEnabled: config.PiiDetectionEnabled,
        JailbreakDetectionEnabled: config.JailbreakDetectionEnabled,
        AutoRedactPii: config.AutoRedactPii,
        MaxInputLength: config.MaxInputLength,
        PiiPatterns: [.. Guardrails.GuardrailPatterns.PiiPatterns.Select(p => p.Name)],
        JailbreakPatterns: [.. Guardrails.GuardrailPatterns.JailbreakPatterns.Select(p => p.Name)],
        ContentSafety: new ContentSafetyConfigResponse(
            Enabled: config.ContentSafety.Enabled,
            FailPolicy: config.ContentSafety.OnUnavailable.ToString(),
            PromptShieldsEnabled: config.ContentSafety.PromptShieldsEnabled,
            CheckInput: config.ContentSafety.CheckInput,
            CheckOutput: config.ContentSafety.CheckOutput,
            CheckRetrievedKnowledge: config.ContentSafety.CheckRetrievedKnowledge,
            CheckToolResults: config.ContentSafety.CheckToolResults,
            HateThreshold: config.ContentSafety.Thresholds.Hate,
            SexualThreshold: config.ContentSafety.Thresholds.Sexual,
            ViolenceThreshold: config.ContentSafety.Thresholds.Violence,
            SelfHarmThreshold: config.ContentSafety.Thresholds.SelfHarm),
        AgentDefinition: new AgentDefinitionPolicyResponse(
            OnValidationFailure: config.AgentDefinition.OnValidationFailure.ToString(),
            SafetyChecksEnabled: config.AgentDefinition.SafetyChecksEnabled,
            TemperatureMin: config.AgentDefinition.TemperatureBounds.Min,
            TemperatureMax: config.AgentDefinition.TemperatureBounds.Max),
        Status: null);
}

record GuardrailsConfigUpdateDto(
    bool? PiiDetectionEnabled = null,
    bool? JailbreakDetectionEnabled = null,
    bool? AutoRedactPii = null,
    int? MaxInputLength = null,
    ContentSafetyConfigUpdateDto? ContentSafety = null);

/// <summary>Partial Content Safety runtime updates. Endpoint URL is intentionally omitted.</summary>
internal record ContentSafetyConfigUpdateDto(
    string? FailPolicy = null,
    int? HateThreshold = null,
    int? SexualThreshold = null,
    int? ViolenceThreshold = null,
    int? SelfHarmThreshold = null);

/// <summary>Public projection of guardrails configuration. Never exposes endpoint URLs or secrets.</summary>
internal record GuardrailsConfigResponse(
    bool PiiDetectionEnabled,
    bool JailbreakDetectionEnabled,
    bool AutoRedactPii,
    int MaxInputLength,
    IReadOnlyList<string> PiiPatterns,
    IReadOnlyList<string> JailbreakPatterns,
    ContentSafetyConfigResponse ContentSafety,
    AgentDefinitionPolicyResponse AgentDefinition,
    string? Status);

/// <summary>Public projection of Content Safety runtime toggles. Endpoint URL is deliberately absent.</summary>
internal record ContentSafetyConfigResponse(
    bool Enabled,
    string FailPolicy,
    bool PromptShieldsEnabled,
    bool CheckInput,
    bool CheckOutput,
    bool CheckRetrievedKnowledge,
    bool CheckToolResults,
    int HateThreshold,
    int SexualThreshold,
    int ViolenceThreshold,
    int SelfHarmThreshold);

/// <summary>
/// Public projection of the agent-definition load-time policy. Only surfaces
/// operator-facing fields — <c>AllowedModels</c>, <c>AllowedTools</c>, and
/// <c>PrivilegedTools</c> are deployment configuration and are never returned.
/// </summary>
internal record AgentDefinitionPolicyResponse(
    string OnValidationFailure,
    bool SafetyChecksEnabled,
    double TemperatureMin,
    double TemperatureMax);
