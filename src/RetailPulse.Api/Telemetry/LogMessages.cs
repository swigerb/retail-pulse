namespace RetailPulse.Api.Telemetry;

/// <summary>
/// High-performance structured log messages using the LoggerMessage source generator.
/// Covers routing decisions, tool calls, cache operations, and errors.
/// </summary>
public static partial class LogMessages
{
    // ── Routing ─────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, EventId = 1001,
        Message = "Routing decision: intent={Intent}, agent={AgentKey}, confidence={Confidence}, fastPath={FastPath}")]
    public static partial void RoutingDecision(
        ILogger logger, string intent, string agentKey, double confidence, bool fastPath);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1002,
        Message = "Low confidence routing: intent={Intent}, confidence={Confidence}, falling back to {FallbackAgent}")]
    public static partial void LowConfidenceRouting(
        ILogger logger, string intent, double confidence, string fallbackAgent);

    // ── Tool Calls ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, EventId = 2001,
        Message = "Tool call started: tool={ToolName}, agent={AgentKey}")]
    public static partial void ToolCallStarted(ILogger logger, string toolName, string agentKey);

    [LoggerMessage(Level = LogLevel.Information, EventId = 2002,
        Message = "Tool call completed: tool={ToolName}, durationMs={DurationMs}, resultLength={ResultLength}")]
    public static partial void ToolCallCompleted(
        ILogger logger, string toolName, double durationMs, int resultLength);

    [LoggerMessage(Level = LogLevel.Error, EventId = 2003,
        Message = "Tool call failed: tool={ToolName}, error={ErrorMessage}")]
    public static partial void ToolCallFailed(ILogger logger, string toolName, string errorMessage, Exception? exception);

    // ── Cache Operations ────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, EventId = 3001,
        Message = "Cache hit: key={CacheKey}")]
    public static partial void CacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, EventId = 3002,
        Message = "Cache miss: key={CacheKey}")]
    public static partial void CacheMiss(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, EventId = 3003,
        Message = "Cache set: key={CacheKey}, ttlSeconds={TtlSeconds}")]
    public static partial void CacheSet(ILogger logger, string cacheKey, double ttlSeconds);

    // ── Errors ──────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Error, EventId = 4001,
        Message = "Agent execution error: agent={AgentKey}, category={ErrorCategory}")]
    public static partial void AgentExecutionError(
        ILogger logger, string agentKey, string errorCategory, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, EventId = 4002,
        Message = "Rate limit exceeded for agent={AgentKey}, retryAfterMs={RetryAfterMs}")]
    public static partial void RateLimitExceeded(ILogger logger, string agentKey, long retryAfterMs);

    // ── Health Checks ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, EventId = 5001,
        Message = "Health check degraded: component={Component}, reason={Reason}")]
    public static partial void HealthCheckDegraded(ILogger logger, string component, string reason);
}
