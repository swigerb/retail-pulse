using System.Collections.Concurrent;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Api.Guardrails;

/// <summary>
/// In-memory ring buffer implementation of ISuspiciousRequestLog.
/// Thread-safe. Evicts oldest entries when max capacity is reached.
/// </summary>
public class InMemorySuspiciousRequestLog : ISuspiciousRequestLog
{
    private readonly ConcurrentQueue<SuspiciousRequest> _entries = new();
    private readonly int _maxEntries;
    private int _jailbreakCount;
    private int _piiCount;
    private int _accessDenialCount;
    private int _contentSafetyBlocks;
    private int _contentSafetyFlags;
    private int _otherCount;
    private readonly DateTime _since = DateTime.UtcNow;

    public InMemorySuspiciousRequestLog(int maxEntries = 100)
    {
        _maxEntries = maxEntries;
    }

    public Task LogAsync(SuspiciousRequest request, CancellationToken ct = default)
    {
        _entries.Enqueue(request);

        // Every audit row must land in exactly one counter. A row that matches
        // nothing is invisible in the header cards while still being drawn in
        // the family and severity charts, which the client derives from the log
        // itself, so the same screen contradicts itself. That is how blocked
        // injections and every agent-definition event came to read as zero.
        switch (request.DetectionType.ToLowerInvariant())
        {
            // Injection shares the jailbreak toggle, so it shares the counter.
            case PatternDetectionTypes.Jailbreak:
            case PatternDetectionTypes.Injection:
            case AgentDefinitionDetectionTypes.Jailbreak:
                Interlocked.Increment(ref _jailbreakCount);
                break;
            case PatternDetectionTypes.Pii:
                Interlocked.Increment(ref _piiCount);
                break;
            case "access_denial":
            // A definition asking for a tool it was never granted is an access
            // denial in every sense the dashboard means by the word.
            case AgentDefinitionDetectionTypes.PrivilegedGrant:
            case AgentDefinitionDetectionTypes.Policy:
                Interlocked.Increment(ref _accessDenialCount);
                break;
            default:
                if (IsModelBasedSafety(request.DetectionType))
                {
                    // A "flagged" action is a non-blocking Content Safety hit that
                    // must still increment the audit feed; every other content-safety
                    // action (block, dropped chunk, fail-open pass, fail-closed block)
                    // counts against the block counter so the dashboard reflects the
                    // safety-critical decisions.
                    if (string.Equals(request.Action, ContentSafetyActions.Flagged, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _contentSafetyFlags);
                    }
                    else
                    {
                        Interlocked.Increment(ref _contentSafetyBlocks);
                    }
                }
                else
                {
                    // Structural definition rejections and any future detection
                    // type land here. Counting them keeps TotalBlocked honest
                    // rather than silently discarding the row.
                    Interlocked.Increment(ref _otherCount);
                }
                break;
        }

        // Ring buffer eviction
        while (_entries.Count > _maxEntries)
            _entries.TryDequeue(out _);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether a detection type belongs to the model-based safety family. This
    /// must stay in step with <c>classifyBlockFamily</c> in the web client's
    /// safetyDisplay module, which classifies the same rows for the dashboard
    /// charts. When the two disagree, the header cards and the charts on the
    /// same screen report different totals for the same audit feed.
    /// </summary>
    private static bool IsModelBasedSafety(string detectionType) =>
        ContentSafetyDetectionTypes.IsContentSafety(detectionType)
        || string.Equals(detectionType, AgentDefinitionDetectionTypes.ContentSafety, StringComparison.OrdinalIgnoreCase)
        || string.Equals(detectionType, AgentDefinitionDetectionTypes.ContentSafetyUnavailable, StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<SuspiciousRequest>> GetRecentAsync(int count = 50, CancellationToken ct = default)
    {
        var result = _entries
            .Reverse()
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<SuspiciousRequest>>(result);
    }

    public Task<GuardrailsStats> GetStatsAsync(CancellationToken ct = default)
    {
        // Flags are excluded because a flag is explicitly a non-blocking hit.
        // Everything else that was logged is counted, so TotalBlocked can never
        // read lower than the number of blocking rows the audit feed is showing.
        int total = _jailbreakCount + _piiCount + _accessDenialCount + _contentSafetyBlocks + _otherCount;
        return Task.FromResult(new GuardrailsStats(
            TotalBlocked: total,
            JailbreakAttempts: _jailbreakCount,
            PiiDetections: _piiCount,
            AccessDenials: _accessDenialCount,
            Since: _since,
            ContentSafetyBlocks: _contentSafetyBlocks,
            ContentSafetyFlags: _contentSafetyFlags));
    }
}
