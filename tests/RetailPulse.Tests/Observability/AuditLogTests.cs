using FluentAssertions;
using RetailPulse.Api.Observability;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Observability;

/// <summary>
/// Tests for InMemoryAuditLog — logging, querying with filters, stats, ring buffer eviction.
/// </summary>
public class AuditLogTests
{
    private readonly InMemoryAuditLog _log;

    public AuditLogTests()
    {
        _log = new InMemoryAuditLog();
    }

    #region LogAsync + QueryAsync basics

    [Fact]
    public async Task LogEntry_IsQueryableImmediately()
    {
        var entry = MakeEntry("e1", agentId: "agent-1", userId: "user-1", action: "chat");
        await _log.LogAsync(entry);

        var results = await _log.QueryAsync(new AuditQuery());

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("e1");
    }

    [Fact]
    public async Task QueryByAgentId_FiltersCorrectly()
    {
        await _log.LogAsync(MakeEntry("e1", agentId: "agent-1"));
        await _log.LogAsync(MakeEntry("e2", agentId: "agent-2"));
        await _log.LogAsync(MakeEntry("e3", agentId: "agent-1"));

        var results = await _log.QueryAsync(new AuditQuery(AgentId: "agent-1"));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.AgentId.Should().Be("agent-1"));
    }

    [Fact]
    public async Task QueryByUserId_FiltersCorrectly()
    {
        await _log.LogAsync(MakeEntry("e1", userId: "alice"));
        await _log.LogAsync(MakeEntry("e2", userId: "bob"));

        var results = await _log.QueryAsync(new AuditQuery(UserId: "alice"));

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be("alice");
    }

    [Fact]
    public async Task QueryByDateRange_FiltersCorrectly()
    {
        var now = DateTime.UtcNow;
        await _log.LogAsync(MakeEntry("e1", timestamp: now.AddHours(-2)));
        await _log.LogAsync(MakeEntry("e2", timestamp: now.AddHours(-1)));
        await _log.LogAsync(MakeEntry("e3", timestamp: now));

        var results = await _log.QueryAsync(new AuditQuery(
            From: now.AddMinutes(-90),
            To: now.AddMinutes(-30)));

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("e2");
    }

    [Fact]
    public async Task QueryByAction_FiltersCorrectly()
    {
        await _log.LogAsync(MakeEntry("e1", action: "chat"));
        await _log.LogAsync(MakeEntry("e2", action: "tool_call"));
        await _log.LogAsync(MakeEntry("e3", action: "chat"));

        var results = await _log.QueryAsync(new AuditQuery(Action: "chat"));

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryCombinedFilters_AgentIdPlusDateRange()
    {
        var now = DateTime.UtcNow;
        await _log.LogAsync(MakeEntry("e1", agentId: "agent-1", timestamp: now.AddHours(-1)));
        await _log.LogAsync(MakeEntry("e2", agentId: "agent-2", timestamp: now.AddHours(-1)));
        await _log.LogAsync(MakeEntry("e3", agentId: "agent-1", timestamp: now.AddDays(-5)));

        var results = await _log.QueryAsync(new AuditQuery(
            AgentId: "agent-1",
            From: now.AddHours(-2)));

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("e1");
    }

    [Fact]
    public async Task QueryLimit_IsRespected()
    {
        for (int i = 0; i < 20; i++)
            await _log.LogAsync(MakeEntry($"e{i}"));

        var results = await _log.QueryAsync(new AuditQuery(Limit: 5));

        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task Query_ResultsOrderedByTimestampDescending()
    {
        var now = DateTime.UtcNow;
        await _log.LogAsync(MakeEntry("old", timestamp: now.AddHours(-3)));
        await _log.LogAsync(MakeEntry("mid", timestamp: now.AddHours(-1)));
        await _log.LogAsync(MakeEntry("new", timestamp: now));

        var results = await _log.QueryAsync(new AuditQuery());

        results[0].Id.Should().Be("new");
        results[1].Id.Should().Be("mid");
        results[2].Id.Should().Be("old");
    }

    [Fact]
    public async Task QueryByAgentId_CaseInsensitive()
    {
        await _log.LogAsync(MakeEntry("e1", agentId: "Agent-1"));

        var results = await _log.QueryAsync(new AuditQuery(AgentId: "agent-1"));

        results.Should().HaveCount(1);
    }

    #endregion

    #region Ring Buffer Eviction

    [Fact]
    public async Task RingBuffer_EvictsOldest_Beyond5000Entries()
    {
        // Fill beyond capacity
        for (int i = 0; i < 5010; i++)
            await _log.LogAsync(MakeEntry($"e{i}", timestamp: DateTime.UtcNow.AddSeconds(i)));

        var all = await _log.QueryAsync(new AuditQuery(Limit: 6000));

        all.Count.Should().BeLessThanOrEqualTo(5000);
    }

    [Fact]
    public async Task RingBuffer_NewestEntriesPreserved()
    {
        for (int i = 0; i < 5010; i++)
            await _log.LogAsync(MakeEntry($"e{i}", timestamp: DateTime.UtcNow.AddSeconds(i)));

        var latest = await _log.QueryAsync(new AuditQuery(Limit: 1));

        // The most recent entry should be e5009
        latest[0].Id.Should().Be("e5009");
    }

    #endregion

    #region GetStatsAsync

    [Fact]
    public async Task Stats_CountByAgent_Accurate()
    {
        await _log.LogAsync(MakeEntry("e1", agentId: "agent-1"));
        await _log.LogAsync(MakeEntry("e2", agentId: "agent-1"));
        await _log.LogAsync(MakeEntry("e3", agentId: "agent-2"));

        var stats = await _log.GetStatsAsync();

        stats.ByAgent.Should().ContainKey("agent-1").WhoseValue.Should().Be(2);
        stats.ByAgent.Should().ContainKey("agent-2").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task Stats_CountByAction_Accurate()
    {
        await _log.LogAsync(MakeEntry("e1", action: "chat"));
        await _log.LogAsync(MakeEntry("e2", action: "chat"));
        await _log.LogAsync(MakeEntry("e3", action: "tool_call"));
        await _log.LogAsync(MakeEntry("e4", action: "approval"));

        var stats = await _log.GetStatsAsync();

        stats.ByAction.Should().ContainKey("chat").WhoseValue.Should().Be(2);
        stats.ByAction.Should().ContainKey("tool_call").WhoseValue.Should().Be(1);
        stats.ByAction.Should().ContainKey("approval").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task Stats_TotalActions_Accurate()
    {
        for (int i = 0; i < 10; i++)
            await _log.LogAsync(MakeEntry($"e{i}"));

        var stats = await _log.GetStatsAsync();

        stats.TotalActions.Should().Be(10);
    }

    [Fact]
    public async Task Stats_EmptyLog_ReturnsZeros()
    {
        var stats = await _log.GetStatsAsync();

        stats.TotalActions.Should().Be(0);
        stats.ByAgent.Should().BeEmpty();
        stats.ByAction.Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static AuditEntry MakeEntry(
        string id,
        string agentId = "agent-1",
        string userId = "user-1",
        string action = "chat",
        DateTime? timestamp = null)
        => new(id, timestamp ?? DateTime.UtcNow, userId, agentId, action,
            "test input", "test output", 100, TimeSpan.FromMilliseconds(50));

    #endregion
}
