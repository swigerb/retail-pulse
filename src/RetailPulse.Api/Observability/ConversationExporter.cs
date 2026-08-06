using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Api.Observability;

/// <summary>
/// Tracks conversation sessions in-memory and exports them as Markdown or JSON.
/// Sessions are created automatically when the first message is tracked.
/// Bounded by configurable session count and per-session message limits.
/// Thread-safe via lock-per-session for message lists and LRU eviction.
/// </summary>
public class ConversationExporter : IConversationExport
{
    private readonly ConcurrentDictionary<string, TrackedSession> _sessions = new();
    private readonly ObservabilityOptions _options;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConversationExporter(IOptions<ObservabilityOptions> options)
    {
        _options = options.Value;
    }

    public Task<ExportResult> ExportAsync(string sessionId, ExportFormat format, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out TrackedSession? session))
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        List<TrackedMessage> snapshot;
        lock (session.Lock)
        {
            snapshot = [.. session.Messages];
        }

        string content = format switch
        {
            ExportFormat.Markdown => ExportMarkdown(session, snapshot),
            ExportFormat.Json => ExportJson(session, snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        string extension = format == ExportFormat.Markdown ? "md" : "json";
        string fileName = $"session-{sessionId[..Math.Min(8, sessionId.Length)]}.{extension}";

        return Task.FromResult(new ExportResult(content, format, fileName, DateTime.UtcNow));
    }

    public Task<IReadOnlyList<ExportableSession>> ListSessionsAsync(CancellationToken ct = default)
    {
        var sessions = _sessions.Values
            .OrderByDescending(s => Volatile.Read(ref s.LastActivity))
            .Select(s =>
            {
                int count;
                int tokens;
                lock (s.Lock)
                {
                    count = s.Messages.Count;
                    tokens = s.TotalTokens;
                }
                return new ExportableSession(
                    s.SessionId,
                    s.StartedAt,
                    count,
                    [.. s.AgentsUsed],
                    tokens);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ExportableSession>>(sessions);
    }

    /// <summary>
    /// Returns session metadata plus a bounded slice (oldest-first) of the tracked
    /// conversation for preview, or <c>null</c> if the session is unknown. Callers
    /// must treat <c>null</c> as a genuine 404 — there is no silent empty fallback.
    /// </summary>
    public Task<SessionPreview?> GetPreviewAsync(string sessionId, int maxMessages = 20, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out TrackedSession? session))
            return Task.FromResult<SessionPreview?>(null);

        int limit = Math.Max(1, maxMessages);
        List<TrackedMessage> snapshot;
        int total;
        lock (session.Lock)
        {
            total = session.Messages.Count;
            snapshot = [.. session.Messages.Take(limit)];
        }

        IReadOnlyList<PreviewMessage> messages = [.. snapshot.Select(m => new PreviewMessage(m.Role, m.Content, m.Timestamp))];
        return Task.FromResult<SessionPreview?>(new SessionPreview(sessionId, messages, total));
    }

    /// <summary>
    /// Track a message in a session, creating the session if needed.
    /// Enforces per-session message limits and LRU session eviction.
    /// </summary>
    public Task TrackMessageAsync(string sessionId, TrackedMessage message, CancellationToken ct = default)
    {
        TrackedSession session = _sessions.GetOrAdd(sessionId, id => new TrackedSession
        {
            SessionId = id,
            StartedAt = DateTime.UtcNow
        });

        // LRU eviction: if session limit reached and this is a brand-new session, remove oldest
        if (_sessions.Count > _options.MaxSessions)
            EvictOldestSession(sessionId);

        lock (session.Lock)
        {
            if (session.Messages.Count < _options.MaxMessagesPerSession)
            {
                session.Messages.Add(message);
                session.TotalTokens += Math.Max(0, message.Tokens ?? 0);
            }
            // Silently drop messages beyond limit — the session stays usable
        }

        Volatile.Write(ref session.LastActivity, DateTime.UtcNow.Ticks);

        if (!string.IsNullOrEmpty(message.AgentId))
            session.AgentsUsed.Add(message.AgentId);

        return Task.CompletedTask;
    }

    /// <summary>Remove the session with the oldest last-activity timestamp.</summary>
    private void EvictOldestSession(string excludeSessionId)
    {
        string? oldest = _sessions
            .Where(kv => kv.Key != excludeSessionId)
            .OrderBy(kv => Volatile.Read(ref kv.Value.LastActivity))
            .Select(kv => kv.Key)
            .FirstOrDefault();

        if (oldest is not null)
            _sessions.TryRemove(oldest, out _);
    }

    // ── Export formatters ────────────────────────────────────────────────

    private static string ExportMarkdown(TrackedSession session, List<TrackedMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Conversation Export — Session {session.SessionId}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Started:** {session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Messages:** {messages.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Agents:** {string.Join(", ", session.AgentsUsed)}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (TrackedMessage msg in messages)
        {
            string agentLabel = !string.IsNullOrEmpty(msg.AgentId) ? $" [{msg.AgentId}]" : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {msg.Role}{agentLabel} — {msg.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            sb.AppendLine();

            if (msg.ToolCalls is { Count: > 0 })
            {
                sb.AppendLine("**Tool Calls:**");
                foreach (string tool in msg.ToolCalls)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{tool}`");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(msg.Reasoning))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"> **Reasoning:** {msg.Reasoning}");
                sb.AppendLine();
            }

            if (msg.DurationMs.HasValue)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"*Duration: {msg.DurationMs.Value.ToString("F0", CultureInfo.InvariantCulture)}ms*");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ExportJson(TrackedSession session, List<TrackedMessage> messages)
    {
        return JsonSerializer.Serialize(new
        {
            sessionId = session.SessionId,
            startedAt = session.StartedAt,
            messageCount = messages.Count,
            agentsUsed = session.AgentsUsed.ToArray(),
            messages = messages.Select(m => new
            {
                role = m.Role,
                content = m.Content,
                agentId = m.AgentId,
                timestamp = m.Timestamp,
                toolCalls = m.ToolCalls,
                reasoning = m.Reasoning,
                durationMs = m.DurationMs
            })
        }, _jsonOptions);
    }

    // ── Internal types ──────────────────────────────────────────────────

    internal class TrackedSession
    {
        public required string SessionId { get; init; }
        public DateTime StartedAt { get; init; }
        public long LastActivity = DateTime.UtcNow.Ticks;
        public readonly object Lock = new();
        public List<TrackedMessage> Messages { get; } = [];
        public ConcurrentBag<string> AgentsUsed { get; } = [];
        /// <summary>Running total of model tokens across tracked messages (guarded by <see cref="Lock"/>).</summary>
        public int TotalTokens { get; set; }
    }
}

/// <summary>
/// A message tracked within a conversation session.
/// </summary>
public record TrackedMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string? AgentId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public List<string>? ToolCalls { get; init; }
    public string? Reasoning { get; init; }
    public double? DurationMs { get; init; }
    /// <summary>Model tokens attributable to this message (input+output for the turn), if known.</summary>
    public int? Tokens { get; init; }
}
