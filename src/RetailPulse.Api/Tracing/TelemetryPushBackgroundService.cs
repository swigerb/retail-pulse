using Microsoft.AspNetCore.SignalR;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Api.Tracing;

/// <summary>
/// Background service that drains the <see cref="TelemetryPushChannel"/> and pushes
/// telemetry events to SignalR clients. Replaces fire-and-forget Task.Run calls
/// in <see cref="InMemoryTraceCollector"/> with backpressure-aware processing.
/// </summary>
public sealed class TelemetryPushBackgroundService : BackgroundService
{
    private readonly TelemetryPushChannel _channel;
    private readonly IHubContext<TelemetryHub> _hubContext;
    private readonly ILogger<TelemetryPushBackgroundService> _logger;

    public TelemetryPushBackgroundService(
        TelemetryPushChannel channel,
        IHubContext<TelemetryHub> hubContext,
        ILogger<TelemetryPushBackgroundService> logger)
    {
        _channel = channel;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry push background service started");

        await foreach (TelemetryPushItem item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

                switch (item.EventType)
                {
                    case "trace_started":
                        await _hubContext.Clients.All.SendAsync("trace_started", new
                        {
                            traceId = item.TraceId,
                            intent = item.Intent,
                            agentName = item.AgentName,
                            model = item.Model,
                            startTime = item.Timestamp
                        }, linkedCts.Token);
                        break;

                    case "span_completed" when item.Span is not null:
                        await _hubContext.Clients.All.SendAsync("span_completed", new
                        {
                            traceId = item.Span.TraceId,
                            span = new
                            {
                                id = item.Span.SpanId,
                                name = item.Span.OperationName,
                                type = item.Span.Tags is not null && item.Span.Tags.TryGetValue("span.type", out string? spanType) ? spanType : "generic",
                                durationMs = item.Span.DurationMs,
                                startTime = item.Span.StartTime,
                                endTime = item.Span.EndTime,
                                inputTokens = item.Span.InputTokens,
                                outputTokens = item.Span.OutputTokens,
                                tags = item.Span.Tags
                            }
                        }, linkedCts.Token);
                        break;
                    default:
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry push failed for {EventType}", item.EventType);
            }
        }

        _logger.LogInformation("Telemetry push background service stopped (dropped={DroppedCount})", _channel.DroppedCount);
    }
}
