using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Api.Agents;

/// <summary>
/// Wraps an <see cref="AIFunction"/> to emit real-time SignalR progress events
/// with accurate per-tool timing. The wrapped tool delegates to the original
/// implementation while instrumenting invocation start/end with a Stopwatch.
/// <para>
/// This allows the existing auto-invocation middleware (UseFunctionInvocation)
/// to continue managing the tool-calling loop while providing granular progress
/// feedback to the frontend via the TelemetryHub.
/// </para>
/// </summary>
public sealed class InstrumentedToolMiddleware
{
    private readonly IHubContext<TelemetryHub> _hubContext;

    public InstrumentedToolMiddleware(IHubContext<TelemetryHub> hubContext)
    {
        _hubContext = hubContext;
    }

    /// <summary>
    /// Wraps a collection of <see cref="AITool"/> instances with instrumentation.
    /// Only <see cref="AIFunction"/> tools are wrapped; other tool types pass through unchanged.
    /// </summary>
    public IReadOnlyList<AITool> WrapTools(IEnumerable<AITool> tools, string sessionId)
    {
        return [.. tools.Select(tool => tool is AIFunction fn
            ? new InstrumentedAIFunction(fn, _hubContext, sessionId)
            : tool)];
    }
}

/// <summary>
/// Instrumented wrapper around an <see cref="AIFunction"/> that emits SignalR
/// progress events with real per-tool wall-clock timing.
/// </summary>
internal sealed class InstrumentedAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly string _sessionId;

    public InstrumentedAIFunction(AIFunction inner, IHubContext<TelemetryHub> hubContext, string sessionId)
    {
        _inner = inner;
        _hubContext = hubContext;
        _sessionId = sessionId;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => _inner.JsonSchema;
    public override System.Text.Json.JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        string toolName = _inner.Name;

        // Emit tool_call started
        await _hubContext.Clients.Group(_sessionId).SendAsync("progress", new
        {
            sessionId = _sessionId,
            phase = "tool_call",
            tool = toolName,
            status = "started",
            detail = $"Calling {toolName}...",
            timestamp = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();

        object? result;
        try
        {
            result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            ToolInvocationTimings.Record(toolName, sw.ElapsedMilliseconds);

            // Emit tool_call failed
            await _hubContext.Clients.Group(_sessionId).SendAsync("progress", new
            {
                sessionId = _sessionId,
                phase = "tool_result",
                tool = toolName,
                status = "failed",
                detail = $"{toolName} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}",
                duration_ms = sw.ElapsedMilliseconds,
                timestamp = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            // Return error as tool result so the LLM can recover gracefully
            // instead of crashing the entire request pipeline
            return $"{{\"error\": \"Tool '{toolName}' failed: {ex.Message}. Please check the required parameters and try again.\"}}";
        }

        sw.Stop();
        ToolInvocationTimings.Record(toolName, sw.ElapsedMilliseconds);

        // Emit tool_call completed with real timing
        await _hubContext.Clients.Group(_sessionId).SendAsync("progress", new
        {
            sessionId = _sessionId,
            phase = "tool_result",
            tool = toolName,
            status = "completed",
            detail = $"{toolName} completed ({sw.ElapsedMilliseconds}ms)",
            duration_ms = sw.ElapsedMilliseconds,
            timestamp = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);

        return result;
    }
}

/// <summary>
/// Minimal timing wrapper for <see cref="AIFunction"/> instances that only need accurate
/// per-invocation duration capture (used by the non-streaming execution path, which doesn't
/// emit SignalR progress events for each tool call).
/// </summary>
internal sealed class TimedAIFunction : AIFunction
{
    private readonly AIFunction _inner;

    public TimedAIFunction(AIFunction inner)
    {
        _inner = inner;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override System.Text.Json.JsonElement JsonSchema => _inner.JsonSchema;
    public override System.Text.Json.JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            object? result = await _inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            ToolInvocationTimings.Record(_inner.Name, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            ToolInvocationTimings.Record(_inner.Name, sw.ElapsedMilliseconds);
            return $"{{\"error\": \"Tool '{_inner.Name}' failed: {ex.Message}. Please check the required parameters and try again.\"}}";
        }
    }
}

