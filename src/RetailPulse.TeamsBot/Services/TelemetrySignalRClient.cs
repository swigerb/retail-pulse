using Microsoft.AspNetCore.SignalR.Client;
using RetailPulse.Contracts;
using System.Collections.Concurrent;

namespace RetailPulse.TeamsBot.Services;

/// <summary>
/// SignalR client for receiving real-time telemetry spans from the RetailPulse API.
/// Spans are now scoped to per-session SignalR groups, so this client must call
/// JoinSession before it will receive anything for a given conversation.
/// </summary>
public class TelemetrySignalRClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<TelemetrySignalRClient> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentSpan>> _spanCollections = new();
    private bool _isConnected;

    public TelemetrySignalRClient(HubConnection connection, ILogger<TelemetrySignalRClient> logger)
    {
        _connection = connection;
        _logger = logger;

        // The API emits a single AgentSpan object (matching the Web client
        // contract). Match that shape exactly — the previous six-positional-arg
        // overload silently dropped every span.
        _connection.On<AgentSpan>("SpanReceived", span =>
        {
            var sessionId = span.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger.LogDebug("Received span without SessionId; dropping. Type={Type}, Name={Name}", span.Type, span.Name);
                return;
            }

            var queue = _spanCollections.GetOrAdd(sessionId, _ => new ConcurrentQueue<AgentSpan>());
            queue.Enqueue(span);
            _logger.LogDebug("Received span for session {SessionId}: {Type} - {Name}", sessionId, span.Type, span.Name);
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
    /// Starts collecting spans for a session. Joins the matching SignalR group
    /// so the API can route session-scoped telemetry to this client.
    /// </summary>
    public async Task StartCollectingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _spanCollections.GetOrAdd(sessionId, _ => new ConcurrentQueue<AgentSpan>());

        if (_isConnected)
        {
            try
            {
                await _connection.InvokeAsync("JoinSession", sessionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to join SignalR session group {SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// Synchronous alias kept for callers that don't want to await.
    /// </summary>
    public void StartCollecting(string sessionId)
        => _ = StartCollectingAsync(sessionId);

    /// <summary>
    /// Gets collected spans for a session and optionally clears them.
    /// </summary>
    public List<AgentSpan> GetSpans(string sessionId, bool clearAfterRead = true)
    {
        if (_spanCollections.TryGetValue(sessionId, out var queue))
        {
            var result = queue.ToList();
            if (clearAfterRead)
            {
                _spanCollections.TryRemove(sessionId, out _);
                _ = LeaveSessionAsync(sessionId);
            }
            return result;
        }

        return new List<AgentSpan>();
    }

    private async Task LeaveSessionAsync(string sessionId)
    {
        if (!_isConnected) return;
        try
        {
            await _connection.InvokeAsync("LeaveSession", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to leave SignalR session group {SessionId}", sessionId);
        }
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
