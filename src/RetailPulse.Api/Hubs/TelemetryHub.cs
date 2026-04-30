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
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    /// <summary>
    /// Removes the caller from a session group.
    /// </summary>
    public Task LeaveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }
}
