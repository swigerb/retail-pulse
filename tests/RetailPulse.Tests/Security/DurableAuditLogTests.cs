using FluentAssertions;
using RetailPulse.Api.Security;
using RetailPulse.Contracts.Observability;

namespace RetailPulse.Tests.Security;

/// <summary>
/// Tests for DurableAuditLog verifying append-only persistence and integrity chain.
/// </summary>
public class DurableAuditLogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DurableAuditLog _auditLog;

    public DurableAuditLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}.db");
        _auditLog = new DurableAuditLog(_dbPath);
    }

    [Fact]
    public async Task LogAsync_PersistsEntry()
    {
        var entry = CreateEntry("test-action");

        await _auditLog.LogAsync(entry);

        var results = await _auditLog.QueryAsync(new AuditQuery(Limit: 10));
        results.Should().HaveCount(1);
        results[0].Action.Should().Be("test-action");
    }

    [Fact]
    public async Task VerifyIntegrity_ReturnsTrueForUntamperedChain()
    {
        await _auditLog.LogAsync(CreateEntry("action-1"));
        await _auditLog.LogAsync(CreateEntry("action-2"));
        await _auditLog.LogAsync(CreateEntry("action-3"));

        var isValid = _auditLog.VerifyIntegrity();

        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyIntegrity_ReturnsFalseAfterTampering()
    {
        await _auditLog.LogAsync(CreateEntry("action-1"));
        await _auditLog.LogAsync(CreateEntry("action-2"));
        await _auditLog.LogAsync(CreateEntry("action-3"));

        // Tamper with the database directly
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE audit_log SET input_summary = 'TAMPERED' WHERE action = 'action-2'";
        cmd.ExecuteNonQuery();

        var isValid = _auditLog.VerifyIntegrity();

        isValid.Should().BeFalse("a tampered entry should break the hash chain");
    }

    [Fact]
    public async Task QueryAsync_FiltersByAgent()
    {
        await _auditLog.LogAsync(CreateEntry("action-1", agentId: "agent-a"));
        await _auditLog.LogAsync(CreateEntry("action-2", agentId: "agent-b"));
        await _auditLog.LogAsync(CreateEntry("action-3", agentId: "agent-a"));

        var results = await _auditLog.QueryAsync(new AuditQuery(AgentId: "agent-a"));

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(e => e.AgentId.Should().Be("agent-a"));
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectCounts()
    {
        await _auditLog.LogAsync(CreateEntry("chat.general", agentId: "general"));
        await _auditLog.LogAsync(CreateEntry("chat.general", agentId: "general"));
        await _auditLog.LogAsync(CreateEntry("chat.demand", agentId: "demand"));

        var stats = await _auditLog.GetStatsAsync();

        stats.TotalActions.Should().Be(3);
        stats.ByAgent["general"].Should().Be(2);
        stats.ByAgent["demand"].Should().Be(1);
    }

    [Fact]
    public async Task EmptyLog_VerifyIntegrity_ReturnsTrue()
    {
        var isValid = _auditLog.VerifyIntegrity();
        isValid.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void ComputeChecksum_IsDeterministic()
    {
        var checksum1 = DurableAuditLog.ComputeChecksum("prev", "data");
        var checksum2 = DurableAuditLog.ComputeChecksum("prev", "data");

        checksum1.Should().Be(checksum2);
    }

    [Fact]
    public void ComputeChecksum_DifferentInputs_ProduceDifferentHashes()
    {
        var checksum1 = DurableAuditLog.ComputeChecksum("prev1", "data");
        var checksum2 = DurableAuditLog.ComputeChecksum("prev2", "data");

        checksum1.Should().NotBe(checksum2);
    }

    private static AuditEntry CreateEntry(string action, string agentId = "test-agent")
    {
        return new AuditEntry(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            "user-1",
            agentId,
            action,
            "test input",
            "test output",
            100,
            TimeSpan.FromMilliseconds(500));
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        try { File.Delete(_dbPath); } catch { }
        GC.SuppressFinalize(this);
    }
}
