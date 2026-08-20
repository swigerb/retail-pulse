namespace RetailPulse.Api.Agents.Planning;

/// <summary>
/// A single step in a plan-first orchestration plan. Emitted by
/// <see cref="PlanBuilder"/> from the planner LLM response and consumed by
/// <see cref="PlanExecutor"/> during workflow execution. Deliberately record-
/// shaped so it can be materialized cheaply into a
/// <see cref="Persistence.PlanStepWrite"/> for the durable plan store without
/// extra copying.
/// </summary>
public sealed record PlannerStep
{
    /// <summary>Specialist key drawn from the live roster (never invented by the planner).</summary>
    public required string SpecialistKey { get; init; }

    /// <summary>Router intent label associated with the step (e.g. "scorecard/portfolio").</summary>
    public required string Intent { get; init; }

    /// <summary>Short imperative directed at the specialist. Preserves user context but never carries tool calls.</summary>
    public required string Action { get; init; }
}

/// <summary>Result of running the planner LLM through <see cref="PlanBuilder"/>.</summary>
public sealed record PlanBuildResult
{
    /// <summary>Ordered planner steps (1..MaxStepCount). Empty when the planner produced an unusable response.</summary>
    public required IReadOnlyList<PlannerStep> Steps { get; init; }

    /// <summary>True when the planner emitted no usable steps (empty, invalid keys, over the cap, or unparseable).</summary>
    public bool IsUnusable => Steps.Count == 0;

    /// <summary>Short reason the plan was flagged unusable — surfaced for logs/telemetry, never for end-user prose.</summary>
    public string? UnusableReason { get; init; }

    /// <summary>Token attribution captured from the planner call, forwarded to the plan cost record.</summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
}
