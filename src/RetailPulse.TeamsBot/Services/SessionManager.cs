using System.Collections.Concurrent;
using RetailPulse.Contracts;

namespace RetailPulse.TeamsBot.Services;

/// <summary>
/// Manages session IDs and telemetry spans for Teams conversations
/// </summary>
public class SessionManager
{
    private readonly ConcurrentDictionary<string, string> _conversationToSession = new();
    private readonly ConcurrentDictionary<string, List<AgentSpan>> _sessionSpans = new();

    /// <summary>
    /// Gets or creates a session ID for a Teams conversation
    /// </summary>
    public string GetOrCreateSessionId(string conversationId)
    {
        return _conversationToSession.GetOrAdd(conversationId, _ => Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Stores telemetry spans for a session
    /// </summary>
    public void StoreSpans(string sessionId, List<AgentSpan> spans)
    {
        _sessionSpans[sessionId] = spans;
    }

    /// <summary>
    /// Retrieves stored spans for a session
    /// </summary>
    public List<AgentSpan>? GetSpans(string sessionId)
    {
        return _sessionSpans.TryGetValue(sessionId, out var spans) ? spans : null;
    }

    /// <summary>
    /// Clears the session for a conversation (e.g., when user says "new chat")
    /// </summary>
    public void ClearSession(string conversationId)
    {
        if (_conversationToSession.TryRemove(conversationId, out var sessionId))
        {
            _sessionSpans.TryRemove(sessionId, out _);
        }
    }
}
