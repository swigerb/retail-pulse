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

    public static void Record(GatewayCallEvidence evidence)
    {
        Current.Value?.Add(evidence);
    }

    public sealed class Scope
    {
        private readonly ConcurrentQueue<GatewayCallEvidence> _calls = new();
        private int _count;

        internal void Add(GatewayCallEvidence evidence)
        {
            if (Interlocked.Increment(ref _count) <= MaxCallsPerScope)
            {
                _calls.Enqueue(evidence);
            }
        }

        /// <summary>Calls that returned no gateway headers at all are counted but carry no evidence.</summary>
        public GatewayEvidence Snapshot()
        {
            GatewayCallEvidence[] calls = [.. _calls];
            if (calls.Length == 0)
            {
                return GatewayEvidence.None;
            }

            GatewayCallEvidence[] reported = [.. calls.Where(c => !c.IsEmpty)];
            long? consumed = reported.Any(c => c.TokensConsumed is not null)
                ? reported.Sum(c => c.TokensConsumed ?? 0)
                : null;
            long? remaining = reported.LastOrDefault(c => c.TokensRemaining is not null)?.TokensRemaining;

            return new GatewayEvidence(reported.Length > 0, calls.Length, consumed, remaining, calls);
        }
    }
}
