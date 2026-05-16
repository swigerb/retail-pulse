namespace RetailPulse.Api.Agents;

/// <summary>
/// Scoped ambient signal that tells the <see cref="AgentExecutionPipeline"/> to use
/// instrumented tools with real-time SignalR progress events and streaming delivery.
/// <para>
/// Registered as Scoped so it is available for the lifetime of a single HTTP request.
/// The streaming endpoint enables it before calling the specialist agent, and the
/// pipeline checks it to decide whether to use <see cref="IAgentExecutionPipeline.ExecuteWithProgressAsync"/>.
/// </para>
/// </summary>
public sealed class StreamingProgressFeature
{
    /// <summary>Whether streaming progress mode is active for this request.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>The session ID to use for SignalR group targeting.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Enables streaming progress mode for the current request scope.</summary>
    public void Enable(string sessionId)
    {
        IsEnabled = true;
        SessionId = sessionId;
    }
}
