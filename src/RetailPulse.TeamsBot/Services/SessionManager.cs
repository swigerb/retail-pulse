using System.Collections.Concurrent;
using RetailPulse.Contracts;

namespace RetailPulse.TeamsBot.Services;

/// <summary>
/// Manages session IDs and telemetry spans for Teams conversations.
/// Entries older than 2 hours are automatically evicted every 30 minutes.
/// </summary>
public class SessionManager : IDisposable
{
    private static readonly TimeSpan _expirationThreshold = TimeSpan.FromHours(2);
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SessionEntry<string>> _conversationToSession = new();
    private readonly ConcurrentDictionary<string, SessionEntry<List<AgentSpan>>> _sessionSpans = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public SessionManager()
    {
        _cleanupTimer = new Timer(_ => EvictExpiredEntries(), null, _cleanupInterval, _cleanupInterval);
    }

    /// <summary>
    /// Gets or creates a session ID for a Teams conversation
    /// </summary>
    public string GetOrCreateSessionId(string conversationId)
    {
        var entry = _conversationToSession.AddOrUpdate(
            conversationId,
            _ => new SessionEntry<string>(Guid.NewGuid().ToString()),
            (_, existing) => { existing.Touch(); return existing; });
        return entry.Value;
    }

    /// <summary>
    /// Stores telemetry spans for a session
    /// </summary>
    public void StoreSpans(string sessionId, List<AgentSpan> spans)
    {
        _sessionSpans.AddOrUpdate(
            sessionId,
            _ => new SessionEntry<List<AgentSpan>>(spans),
            (_, existing) => { existing.Value = spans; existing.Touch(); return existing; });
    }

    /// <summary>
    /// Retrieves stored spans for a session
    /// </summary>
    public List<AgentSpan>? GetSpans(string sessionId)
    {
        if (_sessionSpans.TryGetValue(sessionId, out var entry))
        {
            entry.Touch();
            return entry.Value;
        }
        return null;
    }

    /// <summary>
    /// Clears the session for a conversation (e.g., when user says "new chat")
    /// </summary>
    public void ClearSession(string conversationId)
    {
        if (_conversationToSession.TryRemove(conversationId, out var sessionEntry))
        {
            _sessionSpans.TryRemove(sessionEntry.Value, out _);
        }
    }

    private void EvictExpiredEntries()
    {
        var cutoff = DateTime.UtcNow - _expirationThreshold;

        foreach (var kvp in _conversationToSession)
        {
            if (kvp.Value.LastAccessed < cutoff)
            {
                if (_conversationToSession.TryRemove(kvp.Key, out var removed))
                    _sessionSpans.TryRemove(removed.Value, out _);
            }
        }

        foreach (var kvp in _sessionSpans)
        {
            if (kvp.Value.LastAccessed < cutoff)
                _sessionSpans.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer.Dispose();
            _disposed = true;
        }
    }

    private sealed class SessionEntry<T>
    {
        public T Value { get; set; }
        public DateTime LastAccessed { get; private set; }

        public SessionEntry(T value)
        {
            Value = value;
            LastAccessed = DateTime.UtcNow;
        }

        public void Touch() => LastAccessed = DateTime.UtcNow;
    }
}
