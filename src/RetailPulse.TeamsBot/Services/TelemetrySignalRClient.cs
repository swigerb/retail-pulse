using Microsoft.AspNetCore.SignalR.Client;
using RetailPulse.TeamsBot.Models;
using System.Collections.Concurrent;

namespace RetailPulse.TeamsBot.Services;

/// <summary>
/// SignalR client for receiving real-time telemetry spans from the RetailPulse API
/// </summary>
public class TelemetrySignalRClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<TelemetrySignalRClient> _logger;
    private readonly ConcurrentDictionary<string, List<AgentSpan>> _spanCollections = new();
    private bool _isConnected;

    public TelemetrySignalRClient(HubConnection connection, ILogger<TelemetrySignalRClient> logger)
    {
        _connection = connection;
        _logger = logger;

        // Register handler for span events
        _connection.On<string, string, string, string, double, DateTimeOffset>("SpanReceived", 
            (sessionId, name, type, detail, durationMs, timestamp) =>
            {
                var span = new AgentSpan(name, type, detail, durationMs, timestamp);
                
                if (!_spanCollections.ContainsKey(sessionId))
                {
                    _spanCollections[sessionId] = new List<AgentSpan>();
                }

                _spanCollections[sessionId].Add(span);
                _logger.LogDebug("Received span for session {SessionId}: {Type} - {Name}", sessionId, type, name);
            });
    }

    /// <summary>
    /// Connects to the SignalR hub
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected) return;

        try
        {
            await _connection.StartAsync(cancellationToken);
            _isConnected = true;
            _logger.LogInformation("Connected to telemetry SignalR hub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to telemetry SignalR hub");
        }
    }

    /// <summary>
    /// Starts collecting spans for a session
    /// </summary>
    public void StartCollecting(string sessionId)
    {
        _spanCollections[sessionId] = new List<AgentSpan>();
    }

    /// <summary>
    /// Gets collected spans for a session and optionally clears them
    /// </summary>
    public List<AgentSpan> GetSpans(string sessionId, bool clearAfterRead = true)
    {
        if (_spanCollections.TryGetValue(sessionId, out var spans))
        {
            var result = new List<AgentSpan>(spans);
            if (clearAfterRead)
            {
                _spanCollections.TryRemove(sessionId, out _);
            }
            return result;
        }

        return new List<AgentSpan>();
    }

    /// <summary>
    /// Waits briefly for any remaining spans to arrive
    /// </summary>
    public async Task WaitForSpansAsync(int delayMs = 500, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
