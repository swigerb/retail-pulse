using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using RetailPulse.Contracts;

namespace RetailPulse.TeamsBot.Services;

/// <summary>
/// SignalR client for receiving real-time telemetry spans from the RetailPulse API.
/// Surfaces degraded health when the SignalR connection fails, with exponential
/// backoff reconnection. Health mode is configurable: "fail-fast" vs "degraded".
/// </summary>
public class TelemetrySignalRClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<TelemetrySignalRClient> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentSpan>> _spanCollections = new();
    private readonly string _healthMode;
    private int _reconnectAttempts;

    // Exponential backoff parameters
    internal static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    internal const double ReconnectBackoffMultiplier = 2.0;
    internal const int MaxReconnectAttempts = 10;

    /// <summary>True when the SignalR connection is alive.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>True when the connection is degraded (disconnected but not in fail-fast mode).</summary>
    public bool IsDegraded => !IsConnected && _healthMode != "fail-fast";

    public TelemetrySignalRClient(HubConnection connection, ILogger<TelemetrySignalRClient> logger, string healthMode = "degraded")
    {
        _connection = connection;
        _logger = logger;
        _healthMode = healthMode;

        _connection.On<AgentSpan>("SpanReceived", span =>
        {
            string? sessionId = span.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger.LogDebug("Received span without SessionId; dropping. Type={Type}, Name={Name}", span.Type, span.Name);
                return;
            }

            ConcurrentQueue<AgentSpan> queue = _spanCollections.GetOrAdd(sessionId, _ => new ConcurrentQueue<AgentSpan>());
            queue.Enqueue(span);
            _logger.LogDebug("Received span for session {SessionId}: {Type} - {Name}", sessionId, span.Type, span.Name);
        });

        _connection.Closed += OnConnectionClosed;
        _connection.Reconnected += OnReconnected;
    }

    private Task OnConnectionClosed(Exception? ex)
    {
        IsConnected = false;
        _logger.LogWarning(ex, "SignalR telemetry connection closed. HealthMode={HealthMode}", _healthMode);
        return Task.CompletedTask;
    }

    private Task OnReconnected(string? connectionId)
    {
        IsConnected = true;
        _reconnectAttempts = 0;
        _logger.LogInformation("SignalR telemetry connection restored. ConnectionId={ConnectionId}", connectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connects to the SignalR hub with exponential backoff retry.
    /// In fail-fast mode, throws on failure. In degraded mode, logs and continues.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        TimeSpan delay = InitialReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(cancellationToken);
                IsConnected = true;
                _reconnectAttempts = 0;
                _logger.LogInformation("Connected to telemetry SignalR hub");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("SignalR connection cancelled");
                return; // graceful exit on cancellation in any mode
            }
            catch (Exception ex) when (_reconnectAttempts < MaxReconnectAttempts)
            {
                _reconnectAttempts++;
                _logger.LogWarning(ex,
                    "Failed to connect to telemetry SignalR hub (attempt {Attempt}/{Max}). Retrying in {Delay}ms",
                    _reconnectAttempts, MaxReconnectAttempts, delay.TotalMilliseconds);

                if (_healthMode == "fail-fast" && _reconnectAttempts >= MaxReconnectAttempts)
                    throw;

                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { return; }

                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * ReconnectBackoffMultiplier,
                    MaxReconnectDelay.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to telemetry SignalR hub after {Max} attempts. HealthMode={HealthMode}",
                    MaxReconnectAttempts, _healthMode);

                if (_healthMode == "fail-fast")
                    throw;

                return; // degraded mode: continue without telemetry
            }
        }
    }

    /// <summary>
    /// Starts collecting spans for a session. Joins the matching SignalR group
    /// so the API can route session-scoped telemetry to this client.
    /// </summary>
    public async Task StartCollectingAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _spanCollections.GetOrAdd(sessionId, _ => new ConcurrentQueue<AgentSpan>());

        if (IsConnected)
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
        if (_spanCollections.TryGetValue(sessionId, out ConcurrentQueue<AgentSpan>? queue))
        {
            var result = queue.ToList();
            if (clearAfterRead)
            {
                _spanCollections.TryRemove(sessionId, out _);
                _ = LeaveSessionAsync(sessionId);
            }
            return result;
        }

        return [];
    }

    private async Task LeaveSessionAsync(string sessionId)
    {
        if (!IsConnected) return;
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
    public async Task WaitForSpansAsync(int delayMs = 500, CancellationToken cancellationToken = default) => await Task.Delay(delayMs, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
