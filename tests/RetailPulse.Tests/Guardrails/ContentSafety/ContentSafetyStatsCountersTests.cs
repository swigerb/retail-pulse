using FluentAssertions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A14 — every Content Safety audit row increments the correct counter so the
/// dashboard stat cards reflect blocks vs flags accurately, and blocks add to
/// the aggregate total.
/// </summary>
public class ContentSafetyStatsCountersTests
{
    [Fact]
    public async Task Blocked_IncrementsContentSafetyBlocksAndTotal()
    {
        var log = new InMemorySuspiciousRequestLog();
        await LogAsync(log, ContentSafetyDetectionTypes.Hate, ContentSafetyActions.Blocked);
        await LogAsync(log, ContentSafetyDetectionTypes.PromptShield, ContentSafetyActions.Blocked);
        await LogAsync(log, ContentSafetyDetectionTypes.IndirectInjection, ContentSafetyActions.Dropped);
        await LogAsync(log, ContentSafetyDetectionTypes.Unavailable, ContentSafetyActions.FailClosedBlocked);

        GuardrailsStats stats = await log.GetStatsAsync();
        stats.ContentSafetyBlocks.Should().Be(4);
        stats.ContentSafetyFlags.Should().Be(0);
        stats.TotalBlocked.Should().Be(4, "Content Safety blocks are included in the aggregate total");
    }

    [Fact]
    public async Task Flagged_IncrementsFlagsButNotBlocks()
    {
        var log = new InMemorySuspiciousRequestLog();
        await LogAsync(log, ContentSafetyDetectionTypes.Sexual, ContentSafetyActions.Flagged);

        GuardrailsStats stats = await log.GetStatsAsync();
        stats.ContentSafetyFlags.Should().Be(1);
        stats.ContentSafetyBlocks.Should().Be(0);
        stats.TotalBlocked.Should().Be(0, "Flagged rows are informational and do not count as blocks");
    }

    [Fact]
    public async Task FailOpen_IsCountedAsBlock_ForOperatorVisibility()
    {
        var log = new InMemorySuspiciousRequestLog();
        await LogAsync(log, ContentSafetyDetectionTypes.Unavailable, ContentSafetyActions.FailOpenPassed);

        GuardrailsStats stats = await log.GetStatsAsync();
        // Fail-open still lands in the block counter so a persistent outage is
        // visible on the dashboard even though the request itself continued.
        stats.ContentSafetyBlocks.Should().Be(1);
        stats.ContentSafetyFlags.Should().Be(0);
    }

    private static Task LogAsync(InMemorySuspiciousRequestLog log, string detection, string action) =>
        log.LogAsync(new SuspiciousRequest(
            Id: Guid.NewGuid().ToString("N"),
            Timestamp: DateTime.UtcNow,
            RequestText: "sample",
            DetectionType: detection,
            UserContext: "user",
            Action: action));
}
