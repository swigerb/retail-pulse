namespace RetailPulse.Contracts.Streaming;

/// <summary>
/// A streaming delivery session for token-by-token response delivery.
/// Emits lifecycle events: Start → Token* → Complete | Error.
/// </summary>
public interface IStreamingSession
{
    string SessionId { get; }
    Task EmitStartAsync(string agentId, CancellationToken ct = default);
    Task EmitTokenAsync(string token, int sequenceNumber, CancellationToken ct = default);
    Task EmitCompleteAsync(string fullResponse, CancellationToken ct = default);
    Task EmitErrorAsync(string error, CancellationToken ct = default);
}

/// <summary>
/// Events raised during streaming.
/// </summary>
public record StreamingEvent(string Type, string SessionId, string? Token = null, int? Sequence = null, string? FullResponse = null, string? Error = null);
