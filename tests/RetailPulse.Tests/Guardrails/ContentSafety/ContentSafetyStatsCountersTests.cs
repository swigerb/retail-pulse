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
    public async Task FailOpen_DoesNotCountAsBlock_AndIncrementsFailOpenCounter()
    {
        var log = new InMemorySuspiciousRequestLog();
        await LogAsync(log, ContentSafetyDetectionTypes.Unavailable, ContentSafetyActions.FailOpenPassed);

        GuardrailsStats stats = await log.GetStatsAsync();
        // A fail-open pass ALLOWED the request through; it is the opposite of a
        // block. It must not touch any block counter and must surface on its own
        // FailOpenPasses figure so a degraded safety service is visible.
        stats.FailOpenPasses.Should().Be(1);
        stats.ContentSafetyBlocks.Should().Be(0);
        stats.ContentSafetyFlags.Should().Be(0);
        stats.TotalBlocked.Should().Be(0, "a request allowed through on service failure was not blocked");
    }

    [Fact]
    public async Task LiveScenario_FourFailOpenPassesPlusOneBlock_ReconcilesCounters()
    {
        // Reproduces the exact five audit rows measured on the deployed app: one
        // genuine prompt-shield block and four cold-start fail-open passes.
        var log = new InMemorySuspiciousRequestLog();
        await LogAsync(log, ContentSafetyDetectionTypes.IndirectInjection, ContentSafetyActions.Blocked);
        for (int i = 0; i < 4; i++)
        {
            await LogAsync(log, ContentSafetyDetectionTypes.Unavailable, ContentSafetyActions.FailOpenPassed);
        }

        GuardrailsStats stats = await log.GetStatsAsync();
        stats.TotalBlocked.Should().Be(1, "only one of the five rows was actually blocked");
        stats.ContentSafetyBlocks.Should().Be(1);
        stats.FailOpenPasses.Should().Be(4, "the four cold-start rows were allowed through, not blocked");
        stats.ContentSafetyFlags.Should().Be(0);
        stats.JailbreakAttempts.Should().Be(0);

        // Pattern-based blocks plus model-based blocks must reconcile with the
        // total (structural "other" rejections are zero in this scenario).
        int patternBlocks = stats.JailbreakAttempts + stats.PiiDetections + stats.AccessDenials;
        (patternBlocks + stats.ContentSafetyBlocks).Should().Be(stats.TotalBlocked);
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
