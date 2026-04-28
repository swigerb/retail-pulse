using Microsoft.AspNetCore.SignalR;

namespace RetailPulse.Api.Hubs;

public class TelemetryHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", $"Connected to Retail Pulse telemetry stream");
        await base.OnConnectedAsync();
    }
}
