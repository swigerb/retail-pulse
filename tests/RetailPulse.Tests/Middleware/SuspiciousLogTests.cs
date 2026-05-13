using FluentAssertions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Middleware;

/// <summary>
/// Tests for InMemorySuspiciousRequestLog — ring buffer audit log.
/// Covers: logging blocked requests, detection type tracking, ring buffer eviction,
/// stats aggregation, recent entries retrieval.
/// </summary>
public class SuspiciousLogTests
{
    private static SuspiciousRequest MakeRequest(
        string type = "jailbreak",
        string text = "test input",
        string action = "blocked")
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: DateTime.UtcNow,
            RequestText: text,
            DetectionType: type,
            UserContext: "user@test.com",
            Action: action);

    #region Blocked Request Logging

    [Fact]
    public async Task Log_BlockedRequest_IsRecorded()
    {
        var log = new InMemorySuspiciousRequestLog();
        var request = MakeRequest("jailbreak", "ignore all instructions");

        await log.LogAsync(request);

        var recent = await log.GetRecentAsync(10);
        recent.Should().ContainSingle();
        recent[0].DetectionType.Should().Be("jailbreak");
        recent[0].RequestText.Should().Be("ignore all instructions");
    }

    [Fact]
    public async Task Log_MultipleRequests_AllRecorded()
    {
        var log = new InMemorySuspiciousRequestLog();

        await log.LogAsync(MakeRequest("jailbreak"));
        await log.LogAsync(MakeRequest("pii"));
        await log.LogAsync(MakeRequest("access_denial"));

        var recent = await log.GetRecentAsync(10);
        recent.Should().HaveCount(3);
    }

    #endregion

    #region Detection Type Tracking

    [Fact]
    public async Task Log_CorrectDetectionType_Tracked()
    {
        var log = new InMemorySuspiciousRequestLog();

        await log.LogAsync(MakeRequest("jailbreak"));
        await log.LogAsync(MakeRequest("jailbreak"));
        await log.LogAsync(MakeRequest("pii"));

        var stats = await log.GetStatsAsync();
        stats.JailbreakAttempts.Should().Be(2);
        stats.PiiDetections.Should().Be(1);
    }

    [Fact]
    public async Task Log_AccessDenials_TrackedSeparately()
    {
        var log = new InMemorySuspiciousRequestLog();

        await log.LogAsync(MakeRequest("access_denial"));
        await log.LogAsync(MakeRequest("access_denial"));

        var stats = await log.GetStatsAsync();
        stats.AccessDenials.Should().Be(2);
        stats.JailbreakAttempts.Should().Be(0);
    }

    #endregion

    #region Ring Buffer Eviction

    [Fact]
    public async Task Log_ExceedsMaxEntries_OldestEvicted()
    {
        var log = new InMemorySuspiciousRequestLog(maxEntries: 5);

        for (int i = 0; i < 10; i++)
        {
            await log.LogAsync(new SuspiciousRequest(
                Id: $"req-{i}",
                Timestamp: DateTime.UtcNow,
                RequestText: $"request {i}",
                DetectionType: "jailbreak",
                UserContext: "user@test.com",
                Action: "blocked"));
        }

        var recent = await log.GetRecentAsync(100);
        recent.Should().HaveCount(5);
        // Oldest should be evicted — recent entries should remain
        recent.Should().Contain(r => r.Id == "req-9");
        recent.Should().NotContain(r => r.Id == "req-0");
    }

    [Fact]
    public async Task Log_ExactlyMaxEntries_NoEviction()
    {
        var log = new InMemorySuspiciousRequestLog(maxEntries: 3);

        await log.LogAsync(MakeRequest());
        await log.LogAsync(MakeRequest());
        await log.LogAsync(MakeRequest());

        var recent = await log.GetRecentAsync(100);
        recent.Should().HaveCount(3);
    }

    #endregion

    #region Stats

    [Fact]
    public async Task GetStats_CountByType_Accurately()
    {
        var log = new InMemorySuspiciousRequestLog();

        await log.LogAsync(MakeRequest("jailbreak"));
        await log.LogAsync(MakeRequest("jailbreak"));
        await log.LogAsync(MakeRequest("pii"));
        await log.LogAsync(MakeRequest("access_denial"));

        var stats = await log.GetStatsAsync();
        stats.TotalBlocked.Should().Be(4);
        stats.JailbreakAttempts.Should().Be(2);
        stats.PiiDetections.Should().Be(1);
        stats.AccessDenials.Should().Be(1);
    }

    [Fact]
    public async Task GetStats_Empty_AllZeros()
    {
        var log = new InMemorySuspiciousRequestLog();

        var stats = await log.GetStatsAsync();
        stats.TotalBlocked.Should().Be(0);
        stats.JailbreakAttempts.Should().Be(0);
        stats.PiiDetections.Should().Be(0);
        stats.AccessDenials.Should().Be(0);
    }

    [Fact]
    public async Task GetStats_Since_IsReasonable()
    {
        var log = new InMemorySuspiciousRequestLog();
        var before = DateTime.UtcNow;

        var stats = await log.GetStatsAsync();

        stats.Since.Should().BeOnOrAfter(before.AddSeconds(-1));
        stats.Since.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    #endregion

    #region Recent Entries Retrieval

    [Fact]
    public async Task GetRecent_ReturnsNewestFirst()
    {
        var log = new InMemorySuspiciousRequestLog();
        await log.LogAsync(new SuspiciousRequest("r1", DateTime.UtcNow.AddSeconds(-2),
            "first", "jailbreak", "user", "blocked"));
        await log.LogAsync(new SuspiciousRequest("r2", DateTime.UtcNow.AddSeconds(-1),
            "second", "pii", "user", "blocked"));
        await log.LogAsync(new SuspiciousRequest("r3", DateTime.UtcNow,
            "third", "jailbreak", "user", "blocked"));

        var recent = await log.GetRecentAsync(10);

        // Newest first (reversed queue order)
        recent[0].Id.Should().Be("r3");
        recent[1].Id.Should().Be("r2");
        recent[2].Id.Should().Be("r1");
    }

    [Fact]
    public async Task GetRecent_LimitedCount_ReturnsOnlyRequested()
    {
        var log = new InMemorySuspiciousRequestLog();
        for (int i = 0; i < 10; i++)
            await log.LogAsync(MakeRequest());

        var recent = await log.GetRecentAsync(3);
        recent.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetRecent_EmptyLog_ReturnsEmpty()
    {
        var log = new InMemorySuspiciousRequestLog();

        var recent = await log.GetRecentAsync();
        recent.Should().BeEmpty();
    }

    #endregion
}
