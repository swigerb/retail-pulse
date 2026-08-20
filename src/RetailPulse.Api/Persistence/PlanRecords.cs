namespace RetailPulse.Api.Persistence;

/// <summary>
/// Write envelope for <see cref="IPlanStore.CreatePlanAsync"/>. Carries the
/// initial plan row and its ordered step list.
/// </summary>
public sealed record PlanWrite
{
    public required string PlanId { get; init; }

    /// <summary>
    /// Resolved via <see cref="Auth.UserIdentity.Resolve"/>. Anonymous callers
    /// never reach the store; the endpoint filters those out per policy.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>Optional session the plan belongs to. Kept nullable so a plan can outlive its session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Tenant identifier from <see cref="Contracts.ITenantProvider"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>Original user request that produced the plan.</summary>
    public required string Request { get; init; }

    /// <summary>Router-detected multi-intent signal, persisted for auditability.</summary>
    public IReadOnlyList<string> DetectedIntents { get; init; } = [];

    /// <summary>Initial plan status. Typically <see cref="Contracts.Persistence.PlanStatus.Running"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Ordered step definitions produced by the planner.</summary>
    public required IReadOnlyList<PlanStepWrite> Steps { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Initial step insert accompanying <see cref="PlanWrite"/>.</summary>
public sealed record PlanStepWrite
{
    public required string StepId { get; init; }
    public required int StepIndex { get; init; }
    public required string SpecialistKey { get; init; }
    public required string Intent { get; init; }
    public required string Action { get; init; }

    /// <summary>Initial step status. Typically <see cref="Contracts.Persistence.PlanStepStatus.Pending"/>.</summary>
    public required string Status { get; init; }
}

/// <summary>Update envelope for the plan-level status transition.</summary>
public sealed record PlanStatusUpdate
{
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public string? FailureReason { get; init; }
    public int? TotalInputTokens { get; init; }
    public int? TotalOutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public long? TotalDurationMs { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Update envelope for a single step transition — status, result, tokens, timing.</summary>
public sealed record PlanStepUpdate
{
    public required string StepId { get; init; }
    public required string PlanId { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public long? DurationMs { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
