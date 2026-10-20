using System.Collections.Concurrent;
using RetailPulse.Contracts.Gateway;

namespace RetailPulse.Api.OpenAI.Gateway;

/// <summary>
/// Ambient per-request sink for gateway evidence. The transport policy records into the
/// current scope, so router, specialist, planner, and council calls are all covered
/// without each call site knowing about the gateway.
/// </summary>
public static class GatewayEvidenceCollector
{
    internal const int MaxCallsPerScope = 32;

    private static readonly AsyncLocal<Scope?> Current = new();

    public static Scope Begin()
    {
        var scope = new Scope();
        Current.Value = scope;
        return scope;
    }

    public static void End() => Current.Value = null;

    public static void Record(GatewayCallEvidence evidence) => Current.Value?.Add(evidence);

    public sealed class Scope
    {
        private readonly ConcurrentQueue<GatewayCallEvidence> _calls = new();
        private readonly int[] _statusCounts = new int[Enum.GetValues<GatewayEvidenceStatus>().Length];
        private int _count;

        internal void Add(GatewayCallEvidence evidence)
        {
            // Status counts include calls past the retention cap so a dropped failure still shows in the aggregate.
            Interlocked.Increment(ref _statusCounts[(int)evidence.Status]);
            if (Interlocked.Increment(ref _count) <= MaxCallsPerScope)
            {
                _calls.Enqueue(evidence);
            }
        }

        public GatewayEvidence Snapshot()
        {
            int total = Volatile.Read(ref _count);
            if (total == 0)
            {
                return GatewayEvidence.None;
            }

            GatewayCallEvidence[] calls = [.. _calls];
            int Count(GatewayEvidenceStatus s) => Volatile.Read(ref _statusCounts[(int)s]);

            GatewayEvidenceStatus status =
                Count(GatewayEvidenceStatus.Failed) > 0 ? GatewayEvidenceStatus.Failed
                : Count(GatewayEvidenceStatus.Blocked) > 0 ? GatewayEvidenceStatus.Blocked
                : Count(GatewayEvidenceStatus.Unavailable) > 0 ? GatewayEvidenceStatus.Unavailable
                : Count(GatewayEvidenceStatus.Bypassed) > 0 ? GatewayEvidenceStatus.Bypassed
                : GatewayEvidenceStatus.Observed;

            GatewayCallEvidence[] reported = [.. calls.Where(c => c.Status is GatewayEvidenceStatus.Observed or GatewayEvidenceStatus.Blocked)];
            long? consumed = reported.Any(c => c.TokensConsumed is not null)
                ? reported.Sum(c => c.TokensConsumed ?? 0)
                : null;
            long? remaining = reported.LastOrDefault(c => c.TokensRemaining is not null)?.TokensRemaining;
            int dropped = Math.Max(0, total - calls.Length);

            return new GatewayEvidence(status, total, consumed, remaining, calls, dropped > 0, dropped);
        }
    }
}
