using Microsoft.AspNetCore.SignalR;

namespace RetailPulse.Api.Hubs;

public class TelemetryHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", "Connected to Retail Pulse telemetry stream");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes the caller to spans for a specific chat session.
    /// Used to scope telemetry per session instead of broadcasting to all clients.
    /// </summary>
    public Task JoinSession(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId) ? Task.CompletedTask : Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    /// <summary>
    /// Removes the caller from a session group.
    /// </summary>
    public Task LeaveSession(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId) ? Task.CompletedTask : Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    /// <summary>
    /// Subscribes the caller to card events for a specific card.
    /// Used for per-card SignalR groups so card actions are scoped.
    /// </summary>
    public Task JoinCard(string cardId)
    {
        return string.IsNullOrWhiteSpace(cardId) ? Task.CompletedTask : Groups.AddToGroupAsync(Context.ConnectionId, $"card:{cardId}");
    }

    /// <summary>
    /// Removes the caller from a card group.
    /// </summary>
    public Task LeaveCard(string cardId)
    {
        return string.IsNullOrWhiteSpace(cardId) ? Task.CompletedTask : Groups.RemoveFromGroupAsync(Context.ConnectionId, $"card:{cardId}");
    }
}
