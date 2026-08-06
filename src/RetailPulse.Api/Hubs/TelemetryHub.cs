using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Auth;
using RetailPulse.Api.Security.Anonymous;

namespace RetailPulse.Api.Hubs;

public class TelemetryHub : Hub
{
    private readonly ISessionOwnershipRegistry _ownership;

    public TelemetryHub(ISessionOwnershipRegistry ownership)
    {
        _ownership = ownership;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", "Connected to Retail Pulse telemetry stream");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes the caller to spans for a specific chat session.
    /// Used to scope telemetry per session instead of broadcasting to all clients.
    ///
    /// For an Anonymous caller the session is bound to the caller's immutable subject: the caller may
    /// only join a session it owns (or an as-yet-unclaimed one). This blocks an anonymous attacker
    /// from subscribing to another subject's telemetry with a known/guessed session id (Finding 6).
    /// Entra/dev callers are unchanged.
    /// </summary>
    public Task JoinSession(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? Task.CompletedTask
            : AnonymousCapabilityPolicy.IsAnonymousPrincipal(Context.User)
            && !_ownership.TryBind(sessionId, UserIdentity.Resolve(Context.User))
            ? throw new HubException("Not authorized to join this session.")
            : Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    /// <summary>
    /// Removes the caller from a session group.
    /// </summary>
    public Task LeaveSession(string sessionId) => string.IsNullOrWhiteSpace(sessionId) ? Task.CompletedTask : Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);

    /// <summary>
    /// Subscribes the caller to card events for a specific card.
    /// Used for per-card SignalR groups so card actions are scoped.
    ///
    /// Cards/approvals are not part of the Anonymous surface, so an Anonymous caller is refused here
    /// (deny-by-default) — it cannot subscribe to another subject's card events.
    /// </summary>
    public Task JoinCard(string cardId)
    {
        return string.IsNullOrWhiteSpace(cardId)
            ? Task.CompletedTask
            : AnonymousCapabilityPolicy.IsAnonymousPrincipal(Context.User)
            ? throw new HubException("Card events are not available in Anonymous mode.")
            : Groups.AddToGroupAsync(Context.ConnectionId, $"card:{cardId}");
    }

    /// <summary>
    /// Removes the caller from a card group.
    /// </summary>
    public Task LeaveCard(string cardId) => string.IsNullOrWhiteSpace(cardId) ? Task.CompletedTask : Groups.RemoveFromGroupAsync(Context.ConnectionId, $"card:{cardId}");
}
