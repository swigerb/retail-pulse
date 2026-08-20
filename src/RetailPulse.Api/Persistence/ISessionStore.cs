using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Api.Persistence;

/// <summary>
/// Durable session/turn store. Follows the same shape as the other durable stores
/// (<c>SqliteApprovalGate</c>, <c>SqliteConversationMemory</c>, <c>SqliteAlertService</c>):
/// subject-scoped writes and reads, no cross-subject leakage, deterministic cleanup, and
/// SMB-safe pragmas on every connection via <c>SqliteMount</c>.
///
/// Every read that takes a <c>subject</c> filters at the SQL layer, so a caller can never
/// pull another subject's data even by guessing a session id — a foreign session id
/// resolves to <c>null</c>, which the endpoint layer surfaces as a 404 (never a silent
/// empty success).
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Upsert the session row and append a turn. Both operations run inside a single
    /// SQLite transaction so a partial write cannot leave a session-less turn behind.
    /// </summary>
    Task PersistTurnAsync(SessionTurnWrite write, CancellationToken ct = default);

    /// <summary>
    /// List the caller's sessions, newest activity first, capped by
    /// <see cref="SessionPersistenceOptions.MaxSessionsPerList"/>.
    /// </summary>
    Task<IReadOnlyList<SessionSummaryDto>> ListSessionsForSubjectAsync(
        string subject, CancellationToken ct = default);

    /// <summary>
    /// Return the session and its ordered turns, or <c>null</c> when the id is unknown
    /// or belongs to a different subject. The two failure modes are indistinguishable
    /// by design — a caller must never be able to probe another subject's session id
    /// via a 404-vs-403 oracle.
    /// </summary>
    Task<SessionDetailDto?> GetSessionAsync(
        string subject, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete every row (session + turns) for the given session id when it is owned by
    /// the caller. Returns <c>true</c> when at least one session row was removed, so the
    /// endpoint layer can respond with 204 / 404 without a follow-up existence probe.
    /// </summary>
    Task<bool> DeleteSessionAsync(
        string subject, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Delete sessions whose last activity is older than <paramref name="olderThan"/>.
    /// Turns cascade with the session. Returns per-table row counts so the background
    /// cleanup service can emit observable retention metrics.
    /// </summary>
    Task<CleanupResult> PurgeExpiredAsync(
        DateTimeOffset olderThan, CancellationToken ct = default);
}
