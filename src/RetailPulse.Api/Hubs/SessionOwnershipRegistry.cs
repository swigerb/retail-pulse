using System.Collections.Concurrent;

namespace RetailPulse.Api.Hubs;

/// <summary>
/// Binds a chat <c>sessionId</c> to the immutable subject that owns it, so a SignalR hub can refuse
/// to subscribe a caller to another subject's session group.
///
/// Root cause (Sprint 1 review, Finding 6): <c>Hub.JoinSession(sessionId)</c> added the caller to
/// the group named by an arbitrary, caller-supplied session id. An anonymous attacker who knew or
/// guessed a victim's session id could join that group and receive the victim's streamed tokens and
/// telemetry. This registry records the owner at chat time and lets the hub verify the caller owns
/// (or may claim) the session before joining.
///
/// <para><b>Scope.</b> Ownership binding is consulted only for Anonymous principals; Entra/dev hub
/// semantics are unchanged. Storage is replica-local in-memory (consistent with the other Anonymous
/// guardrails; hosted Anonymous runs at <c>maxReplicas=1</c>) and bounded to avoid unbounded growth.</para>
/// </summary>
public interface ISessionOwnershipRegistry
{
    /// <summary>
    /// Binds <paramref name="sessionId"/> to <paramref name="subject"/> and returns true when the
    /// caller now owns it — either because it was previously unowned (now claimed) or was already
    /// owned by this same subject. Returns false when the session is owned by a DIFFERENT subject.
    /// </summary>
    bool TryBind(string sessionId, string subject);

    /// <summary>Returns the owning subject for a session, or null when unowned. For diagnostics/tests.</summary>
    string? OwnerOf(string sessionId);
}

/// <inheritdoc />
public sealed class SessionOwnershipRegistry : ISessionOwnershipRegistry
{
    private const int _maxEntries = 20_000;

    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    public bool TryBind(string sessionId, string subject)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        EvictIfNeeded();

        string owner = _owners.GetOrAdd(sessionId, subject);
        return string.Equals(owner, subject, StringComparison.Ordinal);
    }

    public string? OwnerOf(string sessionId) =>
        _owners.TryGetValue(sessionId, out string? owner) ? owner : null;

    private void EvictIfNeeded()
    {
        if (_owners.Count < _maxEntries)
        {
            return;
        }

        // Replica-local, bounded: drop a batch of arbitrary entries when the cap is reached. A
        // dropped entry simply means the next claim re-binds ownership; it never widens access.
        foreach (string key in _owners.Keys.Take(_maxEntries / 10))
        {
            _owners.TryRemove(key, out _);
        }
    }
}
