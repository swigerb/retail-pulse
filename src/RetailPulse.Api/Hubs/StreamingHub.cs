using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Auth;

namespace RetailPulse.Api.Hubs;

/// <summary>
/// SignalR hub for real-time agent response streaming.
/// Clients join per-session groups to receive tokens and completion events
/// scoped to their active chat session.
/// </summary>
public class StreamingHub : Hub
{
    private readonly ISessionOwnershipRegistry _ownership;

    public StreamingHub(ISessionOwnershipRegistry ownership)
    {
        _ownership = ownership;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", "Connected to Retail Pulse streaming");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribes the caller to streaming events for a specific chat session.
    ///
    /// Ownership is enforced for BOTH authenticated and anonymous callers (issue #92): every
    /// join and every rejoin binds the sessionId to the caller's immutable subject via
    /// <see cref="ISessionOwnershipRegistry.TryBind"/>. A hostile client that reconnects and
    /// attempts to rejoin another subject's session group is refused. The first join for a
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
            : Groups.AddToGroupAsync(Context.ConnectionId, $"stream:{sessionId}");
    }

    /// <summary>
    /// Removes the caller from a session's streaming group.
    /// </summary>
    public Task LeaveSession(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? Task.CompletedTask
            : Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream:{sessionId}");
    }
}

/// <summary>
/// Typed helper for sending streaming events from backend services.
/// Avoids string-based event names scattered across the codebase.
/// </summary>
public static class StreamingEvents
{
    public const string Start = "streaming:start";
    public const string Token = "streaming:token";
    public const string Complete = "streaming:complete";
    public const string Error = "streaming:error";

    public static Task SendStartAsync(IHubContext<StreamingHub> hub, string sessionId, string agentName)
        => hub.Clients.Group($"stream:{sessionId}").SendAsync(Start, new { sessionId, agentName });

    public static Task SendTokenAsync(IHubContext<StreamingHub> hub, string sessionId, string token, int index)
        => hub.Clients.Group($"stream:{sessionId}").SendAsync(Token, new { sessionId, token, index });

    public static Task SendCompleteAsync(IHubContext<StreamingHub> hub, string sessionId, string fullResponse, bool fromCache)
        => hub.Clients.Group($"stream:{sessionId}").SendAsync(Complete, new { sessionId, fullResponse, fromCache });

    public static Task SendErrorAsync(IHubContext<StreamingHub> hub, string sessionId, string error)
        => hub.Clients.Group($"stream:{sessionId}").SendAsync(Error, new { sessionId, error });
}
