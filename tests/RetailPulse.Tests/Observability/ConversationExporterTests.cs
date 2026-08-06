using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RetailPulse.Api.Configuration;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for <see cref="ConversationExporter"/> — the real registered
/// <see cref="IConversationExport"/>. Covers the preview contract (success /
/// not-found / bounding), token accumulation, session start time, and that
/// Markdown/JSON exports are valid, parseable content.
/// </summary>
public class ConversationExporterTests
{
    private static ConversationExporter CreateExporter(ObservabilityOptions? options = null) =>
        new(Options.Create(options ?? new ObservabilityOptions()));

    private static TrackedMessage User(string content, int? tokens = null) =>
        new() { Role = "user", Content = content, Tokens = tokens };

    private static TrackedMessage Assistant(string content, string agentId, int? tokens = null) =>
        new() { Role = "assistant", Content = content, AgentId = agentId, Tokens = tokens };

    [Fact]
    public async Task GetPreview_UnknownSession_ReturnsNull()
    {
        ConversationExporter exporter = CreateExporter();

        SessionPreview? preview = await exporter.GetPreviewAsync("does-not-exist");

        preview.Should().BeNull("callers must treat null as a genuine 404, not a silent empty success");
    }

    [Fact]
    public async Task GetPreview_KnownSession_ReturnsMetadataAndOldestFirstSlice()
    {
        ConversationExporter exporter = CreateExporter();
        await exporter.TrackMessageAsync("s1", User("first question"));
        await exporter.TrackMessageAsync("s1", Assistant("first answer", "demand-agent"));
        await exporter.TrackMessageAsync("s1", User("second question"));

        SessionPreview? preview = await exporter.GetPreviewAsync("s1");

        preview.Should().NotBeNull();
        preview.SessionId.Should().Be("s1");
        preview.TotalMessages.Should().Be(3);
        preview.Messages.Should().HaveCount(3);
        preview.Messages[0].Role.Should().Be("user");
        preview.Messages[0].Content.Should().Be("first question");
        preview.Messages[1].Role.Should().Be("assistant");
    }

    [Fact]
    public async Task GetPreview_BoundsMessageSlice_ButReportsTrueTotal()
    {
        ConversationExporter exporter = CreateExporter();
        for (int i = 0; i < 10; i++)
            await exporter.TrackMessageAsync("s1", User($"msg {i}"));

        SessionPreview? preview = await exporter.GetPreviewAsync("s1", maxMessages: 3);

        preview.Should().NotBeNull();
        preview.Messages.Should().HaveCount(3);
        preview.TotalMessages.Should().Be(10);
    }

    [Fact]
    public async Task ListSessions_IncludesTotalTokensAndStartTime()
    {
        ConversationExporter exporter = CreateExporter();
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        await exporter.TrackMessageAsync("s1", User("q", tokens: 0));
        await exporter.TrackMessageAsync("s1", Assistant("a", "demand-agent", tokens: 150));

        IReadOnlyList<ExportableSession> sessions = await exporter.ListSessionsAsync();

        ExportableSession session = sessions.Single(s => s.SessionId == "s1");
        session.TotalTokens.Should().Be(150, "token totals must aggregate per-message token counts");
        session.MessageCount.Should().Be(2);
        session.AgentsUsed.Should().Contain("demand-agent");
        session.StartedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Export_Markdown_ProducesValidReadableContent()
    {
        ConversationExporter exporter = CreateExporter();
        await exporter.TrackMessageAsync("s1", User("How are sales?"));
        await exporter.TrackMessageAsync("s1", Assistant("Sales are up 12%.", "demand-agent", tokens: 42));

        ExportResult result = await exporter.ExportAsync("s1", ExportFormat.Markdown);

        result.Format.Should().Be(ExportFormat.Markdown);
        result.FileName.Should().EndWith(".md");
        result.Content.Should().StartWith("# Conversation Export");
        result.Content.Should().Contain("How are sales?");
        result.Content.Should().Contain("Sales are up 12%.");
    }

    [Fact]
    public async Task Export_Json_ProducesParseableJsonWithMessages()
    {
        ConversationExporter exporter = CreateExporter();
        await exporter.TrackMessageAsync("s1", User("How are sales?"));
        await exporter.TrackMessageAsync("s1", Assistant("Sales are up 12%.", "demand-agent"));

        ExportResult result = await exporter.ExportAsync("s1", ExportFormat.Json);

        result.Format.Should().Be(ExportFormat.Json);
        result.FileName.Should().EndWith(".json");

        // Must be genuinely parseable JSON — not Markdown mislabeled as JSON.
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("s1");
        doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Export_UnknownSession_Throws()
    {
        ConversationExporter exporter = CreateExporter();

        Func<Task> act = () => exporter.ExportAsync("nope", ExportFormat.Markdown);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
