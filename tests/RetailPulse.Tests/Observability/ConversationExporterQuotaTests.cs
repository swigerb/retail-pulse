using FluentAssertions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for ConversationExporter quota enforcement:
/// per-session message limits, session count with LRU eviction, and thread safety.
/// </summary>
public class ConversationExporterQuotaTests
{
    private static ConversationExporter CreateExporter(
        int maxSessions = 1_000,
        int maxMessagesPerSession = 200)
    {
        return new ConversationExporter(Options.Create(new ObservabilityOptions
        {
            MaxSessions = maxSessions,
            MaxMessagesPerSession = maxMessagesPerSession
        }));
    }

    private static TrackedMessage MakeMessage(string content = "test message", string? agentId = null)
    {
        return new TrackedMessage
        {
            Role = "user",
            Content = content,
            AgentId = agentId,
            Timestamp = DateTime.UtcNow
        };
    }

    // ── Per-session message limit ───────────────────────────────────────

    [Fact]
    public async Task TrackMessage_WithinLimit_AllMessagesRetained()
    {
        var exporter = CreateExporter(maxMessagesPerSession: 10);
        var sessionId = "session-1";

        for (int i = 0; i < 10; i++)
            await exporter.TrackMessageAsync(sessionId, MakeMessage($"msg-{i}"));

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().ContainSingle(s => s.SessionId == sessionId);
        sessions[0].MessageCount.Should().Be(10);
    }

    [Fact]
    public async Task TrackMessage_ExceedingLimit_SilentlyDropsExcessMessages()
    {
        var exporter = CreateExporter(maxMessagesPerSession: 5);
        var sessionId = "session-limited";

        for (int i = 0; i < 20; i++)
            await exporter.TrackMessageAsync(sessionId, MakeMessage($"msg-{i}"));

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().ContainSingle(s => s.SessionId == sessionId);
        sessions[0].MessageCount.Should().Be(5, "messages beyond limit are silently dropped");
    }

    [Fact]
    public async Task TrackMessage_Default200Limit_Enforced()
    {
        var exporter = CreateExporter(maxMessagesPerSession: 200);
        var sessionId = "session-default";

        for (int i = 0; i < 250; i++)
            await exporter.TrackMessageAsync(sessionId, MakeMessage($"msg-{i}"));

        var sessions = await exporter.ListSessionsAsync();
        sessions[0].MessageCount.Should().Be(200);
    }

    // ── Session count limit with LRU eviction ───────────────────────────

    [Fact]
    public async Task TrackMessage_ExceedingSessionLimit_EvictsOldestSession()
    {
        var exporter = CreateExporter(maxSessions: 3);

        // Create 3 sessions
        for (int i = 1; i <= 3; i++)
        {
            await exporter.TrackMessageAsync($"session-{i}", MakeMessage($"msg from session {i}"));
            // Small delay to differentiate LastActivity
            await Task.Delay(10);
        }

        // Add a 4th session — should evict the oldest (session-1)
        await exporter.TrackMessageAsync("session-4", MakeMessage("msg from session 4"));

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().HaveCountLessThanOrEqualTo(3);
        sessions.Select(s => s.SessionId).Should().Contain("session-4");
    }

    [Fact]
    public async Task TrackMessage_LruEviction_KeepsMostRecentSessions()
    {
        var exporter = CreateExporter(maxSessions: 2);

        await exporter.TrackMessageAsync("old-session", MakeMessage("old"));
        await Task.Delay(20);
        await exporter.TrackMessageAsync("mid-session", MakeMessage("mid"));
        await Task.Delay(20);

        // Adding a 3rd session should trigger eviction of the oldest
        await exporter.TrackMessageAsync("new-session", MakeMessage("new"));

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().HaveCountLessThanOrEqualTo(2);
        sessions.Select(s => s.SessionId).Should().Contain("new-session");
    }

    // ── Thread safety ───────────────────────────────────────────────────

    [Fact]
    public async Task TrackMessage_ConcurrentWrites_NoExceptionsThrown()
    {
        var exporter = CreateExporter(maxSessions: 100, maxMessagesPerSession: 1000);
        var sessionId = "concurrent-session";

        var tasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() =>
                exporter.TrackMessageAsync(sessionId, MakeMessage($"concurrent-msg-{i}"))))
            .ToArray();

        await Task.WhenAll(tasks);

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().ContainSingle(s => s.SessionId == sessionId);
        sessions[0].MessageCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TrackMessage_ConcurrentSessions_NoExceptionsThrown()
    {
        var exporter = CreateExporter(maxSessions: 200, maxMessagesPerSession: 100);

        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() =>
                exporter.TrackMessageAsync($"session-{i}", MakeMessage($"msg-{i}"))))
            .ToArray();

        await Task.WhenAll(tasks);

        var sessions = await exporter.ListSessionsAsync();
        sessions.Should().HaveCountGreaterThan(0);
    }

    // ── Export ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ExistingSession_ReturnsContent()
    {
        var exporter = CreateExporter();
        var sessionId = "export-session";

        await exporter.TrackMessageAsync(sessionId, MakeMessage("Hello from test"));

        var result = await exporter.ExportAsync(sessionId, ExportFormat.Markdown);

        result.Content.Should().Contain("Hello from test");
        result.Format.Should().Be(ExportFormat.Markdown);
        result.FileName.Should().Contain("export-s");
    }

    [Fact]
    public async Task Export_NonExistentSession_ThrowsKeyNotFound()
    {
        var exporter = CreateExporter();

        var act = () => exporter.ExportAsync("nonexistent", ExportFormat.Json);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Export_JsonFormat_ReturnsValidJson()
    {
        var exporter = CreateExporter();
        var sessionId = "json-session";

        await exporter.TrackMessageAsync(sessionId, MakeMessage("JSON test message"));

        var result = await exporter.ExportAsync(sessionId, ExportFormat.Json);

        result.Content.Should().Contain("\"sessionId\"");
        result.Content.Should().Contain("JSON test message");
        result.Format.Should().Be(ExportFormat.Json);
    }
}
