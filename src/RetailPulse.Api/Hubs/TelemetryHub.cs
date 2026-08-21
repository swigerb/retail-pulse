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
    /// Ownership is enforced for BOTH authenticated and anonymous callers (issue #92):
    /// every join and every rejoin binds the sessionId to the caller's immutable subject
    /// via <see cref="ISessionOwnershipRegistry.TryBind"/>. A hostile client that reconnects
    /// and attempts to rejoin another subject's session id is refused. The first join for a
    /// server-minted sessionId claims ownership; every subsequent join (including reconnects)
    /// must match the recorded owner.
    /// </summary>
    public Task JoinSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        string subject = UserIdentity.Resolve(Context.User);
        return !_ownership.TryBind(sessionId, subject)
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
