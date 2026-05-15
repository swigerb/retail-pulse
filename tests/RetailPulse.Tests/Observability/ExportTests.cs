using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for MarkdownExporter (IConversationExport) — Markdown and JSON export, session listing.
/// </summary>
public class ExportTests
{
    private readonly InMemoryAuditLog _auditLog;
    private readonly MarkdownExporter _exporter;

    public ExportTests()
    {
        _auditLog = new InMemoryAuditLog();
        _exporter = new MarkdownExporter(_auditLog);
    }

    #region Export to Markdown

    [Fact]
    public async Task ExportMarkdown_IncludesMessagesWithAttribution()
    {
        await SeedAuditEntries("session-abc");

        var result = await _exporter.ExportAsync("session-abc", ExportFormat.Markdown);

        result.Content.Should().Contain("session-abc");
        result.Content.Should().Contain("agent-1");
        result.Content.Should().Contain("user-1");
        result.Format.Should().Be(ExportFormat.Markdown);
    }

    [Fact]
    public async Task ExportMarkdown_IncludesToolCalls()
    {
        var entry = new AuditEntry(
            "session-tools-1", DateTime.UtcNow, "user-1", "agent-1",
            "tool_call", "GetDepletions query", "Results: 500 cases",
            200, TimeSpan.FromMilliseconds(100));

        await _auditLog.LogAsync(entry);

        var result = await _exporter.ExportAsync("session-tools", ExportFormat.Markdown);

        result.Content.Should().Contain("tool_call");
        result.Content.Should().Contain("GetDepletions");
    }

    [Fact]
    public async Task ExportMarkdown_ContainsHeaderAndTimestamp()
    {
        await SeedAuditEntries("session-hdr");

        var result = await _exporter.ExportAsync("session-hdr", ExportFormat.Markdown);

        result.Content.Should().Contain("# Conversation Export");
        result.Content.Should().Contain("Exported:");
        result.FileName.Should().StartWith("export-session-hdr-");
        result.FileName.Should().EndWith(".md");
    }

    [Fact]
    public async Task ExportMarkdown_IncludesTokenAndDuration()
    {
        var entry = new AuditEntry("session-meta-1", DateTime.UtcNow, "u1", "a1", "chat",
            "input", "output", 150, TimeSpan.FromMilliseconds(250));
        await _auditLog.LogAsync(entry);

        var result = await _exporter.ExportAsync("session-meta", ExportFormat.Markdown);

        result.Content.Should().Contain("150");
        result.Content.Should().Contain("250");
    }

    #endregion

    #region Export to JSON

    [Fact]
    public async Task ExportJson_IsValidJsonWithAllFields()
    {
        await SeedAuditEntries("session-json");

        var result = await _exporter.ExportAsync("session-json", ExportFormat.Json);

        result.Format.Should().Be(ExportFormat.Json);
        result.FileName.Should().EndWith(".json");

        // Validate JSON is parseable
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("session-json");
        doc.RootElement.GetProperty("entryCount").GetInt32().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("entries").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExportJson_EntriesHaveAllExpectedFields()
    {
        var entry = new AuditEntry("session-fields-1", DateTime.UtcNow, "alice", "demand-agent",
            "chat", "What are Q4 trends?", "Demand is up 15%.", 300, TimeSpan.FromMilliseconds(500));
        await _auditLog.LogAsync(entry);

        var result = await _exporter.ExportAsync("session-fields", ExportFormat.Json);
        var doc = JsonDocument.Parse(result.Content);
        var entries = doc.RootElement.GetProperty("entries");
        var first = entries[0];

        // System.Text.Json serializes anonymous type properties as PascalCase by default
        first.TryGetProperty("Id", out _).Should().BeTrue();
        first.TryGetProperty("UserId", out _).Should().BeTrue();
        first.TryGetProperty("AgentId", out _).Should().BeTrue();
        first.TryGetProperty("Action", out _).Should().BeTrue();
        first.TryGetProperty("InputSummary", out _).Should().BeTrue();
        first.TryGetProperty("OutputSummary", out _).Should().BeTrue();
        first.TryGetProperty("TokensUsed", out _).Should().BeTrue();
        first.TryGetProperty("durationMs", out _).Should().BeTrue();
    }

    #endregion

    #region Empty Session

    [Fact]
    public async Task ExportEmptySession_ReturnsMinimalValidOutput()
    {
        // No entries seeded — exporter falls back to recent entries (empty)
        var result = await _exporter.ExportAsync("no-such-session", ExportFormat.Markdown);

        result.Should().NotBeNull();
        result.Content.Should().NotBeNullOrEmpty();
        result.Content.Should().Contain("Conversation Export");
    }

    [Fact]
    public async Task ExportEmptySession_Json_IsValidJson()
    {
        var result = await _exporter.ExportAsync("empty-session", ExportFormat.Json);

        result.Should().NotBeNull();
        var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("empty-session");
    }

    #endregion

    #region ListSessionsAsync

    [Fact]
    public async Task ListSessions_ReturnsCorrectMetadata()
    {
        await SeedAuditEntries("session-list");

        var sessions = await _exporter.ListSessionsAsync();

        sessions.Should().NotBeEmpty();
        sessions.Should().Contain(s => s.MessageCount > 0);
    }

    [Fact]
    public async Task ListSessions_EmptyLog_ReturnsEmpty()
    {
        var sessions = await _exporter.ListSessionsAsync();
        sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessions_IncludesAgentsUsed()
    {
        var entry1 = new AuditEntry("s1-1", DateTime.UtcNow, "u1", "demand-agent", "chat", "in", "out", 100, TimeSpan.Zero);
        var entry2 = new AuditEntry("s1-2", DateTime.UtcNow, "u1", "supply-agent", "chat", "in", "out", 100, TimeSpan.Zero);
        await _auditLog.LogAsync(entry1);
        await _auditLog.LogAsync(entry2);

        var sessions = await _exporter.ListSessionsAsync();

        sessions.Should().NotBeEmpty();
        // At least one session should have agent info
        sessions.Any(s => s.AgentsUsed.Length > 0).Should().BeTrue();
    }

    #endregion

    #region Helpers

    private async Task SeedAuditEntries(string sessionIdPrefix, int count = 3)
    {
        for (int i = 0; i < count; i++)
        {
            var entry = new AuditEntry(
                $"{sessionIdPrefix}-{i}",
                DateTime.UtcNow.AddMinutes(-count + i),
                "user-1", "agent-1", "chat",
                $"Input message {i}", $"Output response {i}",
                100 + (i * 10), TimeSpan.FromMilliseconds(50 + (i * 10)));
            await _auditLog.LogAsync(entry);
        }
    }

    #endregion
}
