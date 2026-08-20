namespace RetailPulse.Api.Persistence;

/// <summary>
/// Configuration for the durable session/turn store.
///
/// Every knob has a sensible default so the feature can ship without any operator ceremony:
/// enabled off by default (Wave 1 behaviour is preserved bit-for-bit until an operator turns it on),
/// a 30-day retention TTL, a one-hour cleanup interval, and PII redaction on write ON by default
/// because conversation content is more sensitive than the memory facts the existing durable
/// stores already hold.
/// </summary>
public sealed class SessionPersistenceOptions
{
    public const string SectionName = "SessionPersistence";

    /// <summary>
    /// Master switch. When false, the store singleton is not registered, the cleanup hosted
    /// service does not run, no database file is created or opened, and the chat pipeline
    /// behaves exactly as it did before the feature was added. The session endpoints are
    /// mapped only when this is true, matching the current feature-off convention used for
    /// Anonymous/GitHub auth (endpoints appear only for the modes that back them).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Retention TTL — sessions whose last activity is older than this are purged by the
    /// background cleanup service. 30 days is the same order as the memory store's
    /// implicit expiry and is a defensible default for a chat history that a user might
    /// reasonably expect to see in the coming weeks. Values less than or equal to
    /// <see cref="TimeSpan.Zero"/> disable purging (retention is unbounded).
    /// </summary>
    public TimeSpan RetentionTtl { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How often the cleanup service scans for expired sessions. Default is one hour —
    /// the same cadence as the memory expiry sweep — which keeps the SQLite DELETE
    /// pressure low on the SMB-safe rollback journal while still keeping purged history
    /// out of a compromised backup within a bounded window.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Apply <see cref="RetailPulse.Api.Guardrails.PiiRedactor"/> to turn content before persistence.
    /// On by default because rehydratable chat transcripts are a higher-value PII target than the
    /// summarized memory rows the memory store already holds — the redactor is the same shared
    /// seam the output guardrail already uses, so redaction on write and redaction on display
    /// stay in lock-step.
    /// </summary>
    public bool RedactPiiOnWrite { get; set; } = true;

    /// <summary>
    /// Hard ceiling on the number of turns returned by <see cref="ISessionStore.GetSessionAsync"/>
    /// so a single rehydrate request can't materialise an unbounded amount of content into
    /// memory. Older turns are dropped first (the newest window survives). The chat endpoint
    /// already trims to the last 10 exchanges before sending to the model, so this cap only
    /// affects the browser's visible transcript.
    /// </summary>
    public int MaxTurnsPerRehydrate { get; set; } = 200;

    /// <summary>
    /// Hard ceiling on the number of sessions returned by <see cref="ISessionStore.ListSessionsForSubjectAsync"/>.
    /// The list endpoint is intended for a sidebar-style history view; more than a couple of
    /// hundred entries is not a UX any operator wants to ship, so this bounds the response
    /// regardless of retention.
    /// </summary>
    public int MaxSessionsPerList { get; set; } = 200;
}
