using System.Collections.Concurrent;
using RetailPulse.Contracts.Consensus;

namespace RetailPulse.Api.Consensus;

/// <summary>
/// Bounded, in-memory record of council verdicts so the Health Council panel's
/// "Load History" action has something to load.
/// </summary>
/// <remarks>
/// The panel always had a history button and <c>councilApi.fetchCouncilHistory</c>
/// always called <c>GET /api/council/history</c>, but no such route was ever mapped,
/// so the button silently produced an empty list forever.
/// <para>
/// This store is deliberately in-memory and per-replica, matching the plan and
/// session stores: the demo has no council database, and inventing one would be a
/// larger change than the feature warrants. History therefore resets when the
/// revision changes or a replica is replaced, which is documented behaviour rather
/// than a defect.
/// </para>
/// </remarks>
public sealed class CouncilHistoryStore
{
    /// <summary>Keeps memory bounded on a long-lived replica.</summary>
    public const int Capacity = 50;

    private readonly ConcurrentQueue<CouncilVerdict> _verdicts = new();

    public void Record(CouncilVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        _verdicts.Enqueue(verdict);

        // Trim to capacity. A concurrent writer may briefly push the queue one over
        // the bound between the count check and the dequeue; that is harmless and
        // preferable to locking on the hot convene path.
        while (_verdicts.Count > Capacity && _verdicts.TryDequeue(out _))
        {
        }
    }

    /// <summary>Most recent verdicts first.</summary>
    public IReadOnlyList<CouncilVerdict> GetRecent(int limit) =>
        [.. _verdicts.Reverse().Take(Math.Clamp(limit, 1, Capacity))];
}
