namespace RetailPulse.Api.Approval;

/// <summary>
/// Configuration for <see cref="SqliteApprovalGate"/>. Every knob has a sensible
/// default so the feature ships without operator ceremony; the only value an
/// operator typically overrides is <see cref="DefaultTimeout"/> when a deployment's
/// human-response SLA is longer or shorter than five minutes.
/// </summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Approval";

    /// <summary>
    /// Authoritative default timeout used when the caller passes no explicit timeout
    /// to <see cref="SqliteApprovalGate.RequestApprovalAsync"/>. This value is
    /// persisted alongside the row so a waiter created in a later process still
    /// honours the timeout the row was created with — the runtime configuration
    /// cannot silently shrink or extend a pending request that a human has already
    /// been notified about. Default: 5 minutes.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often an in-process waiter refreshes its row's <c>HeartbeatAt</c> column so
    /// operators can observe liveness. Reconciliation itself uses the durable
    /// agent-instance id (not the heartbeat) to decide whether a Pending row belongs
    /// to the current process — the heartbeat is purely observability. Default: 30s.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the startup reconciliation service runs. Off would leak stuck-Pending
    /// rows on restart and reintroduce the exact bug this feature exists to fix —
    /// keep it on. Exposed only so tests can register the gate without the hosted
    /// service. Default: true.
    /// </summary>
    public bool ReconcileOnStartup { get; set; } = true;
}
