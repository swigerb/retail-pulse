namespace RetailPulse.Api.Memory;

/// <summary>
/// Work item for background memory extraction via bounded channel.
/// </summary>
public sealed record MemoryWorkItem(
    string UserId,
    string UserMessage,
    string AssistantReply,
    string? TraceId = null,
    string? ParentSpanId = null);
