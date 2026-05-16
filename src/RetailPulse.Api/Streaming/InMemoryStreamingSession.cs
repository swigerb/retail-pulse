using RetailPulse.Contracts.Streaming;

namespace RetailPulse.Api.Streaming;

/// <summary>
/// In-memory streaming session that records events for testing and
/// delivers tokens through an event-based callback model.
/// Supports session grouping — only subscribers to the session receive events.
/// </summary>
public class InMemoryStreamingSession : IStreamingSession
{
    private readonly List<StreamingEvent> _events = [];
    private readonly Lock _lock = new();

    public string SessionId { get; }
    public IReadOnlyList<StreamingEvent> Events
    {
        get { lock (_lock) return [.. _events]; }
    }

    public InMemoryStreamingSession(string sessionId)
    {
        SessionId = sessionId;
    }

    public Task EmitStartAsync(string agentId, CancellationToken ct = default)
    {
        lock (_lock)
            _events.Add(new StreamingEvent("start", SessionId));
        return Task.CompletedTask;
    }

    public Task EmitTokenAsync(string token, int sequenceNumber, CancellationToken ct = default)
    {
        lock (_lock)
            _events.Add(new StreamingEvent("token", SessionId, Token: token, Sequence: sequenceNumber));
        return Task.CompletedTask;
    }

    public Task EmitCompleteAsync(string fullResponse, CancellationToken ct = default)
    {
        lock (_lock)
            _events.Add(new StreamingEvent("complete", SessionId, FullResponse: fullResponse));
        return Task.CompletedTask;
    }

    public Task EmitErrorAsync(string error, CancellationToken ct = default)
    {
        lock (_lock)
            _events.Add(new StreamingEvent("error", SessionId, Error: error));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Simulates streaming a response token by token.
    /// Emits Start → Token* → Complete lifecycle.
    /// </summary>
    public async Task StreamResponseAsync(string agentId, string fullResponse, CancellationToken ct = default)
    {
        await EmitStartAsync(agentId, ct);

        string[] tokens = fullResponse.Split(' ');
        for (int i = 0; i < tokens.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await EmitTokenAsync(tokens[i], i, ct);
        }

        await EmitCompleteAsync(fullResponse, ct);
    }

    /// <summary>
    /// Simulates a non-streaming fallback — returns the full response at once.
    /// </summary>
    public static string GetNonStreamingResponse(string fullResponse) => fullResponse;
}
