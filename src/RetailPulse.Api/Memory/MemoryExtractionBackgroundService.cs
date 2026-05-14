using RetailPulse.Api.Middleware;
using RetailPulse.Api.Observability;
using RetailPulse.Api.Tracing;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Background service that drains the <see cref="MemoryExtractionChannel"/> and
/// processes memory extraction work items using a scoped <see cref="ConversationMemoryMiddleware"/>.
/// </summary>
public sealed class MemoryExtractionBackgroundService : BackgroundService
{
    private readonly MemoryExtractionChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InMemoryTraceCollector _traceCollector;
    private readonly ILogger<MemoryExtractionBackgroundService> _logger;

    public MemoryExtractionBackgroundService(
        MemoryExtractionChannel channel,
        IServiceScopeFactory scopeFactory,
        InMemoryTraceCollector traceCollector,
        ILogger<MemoryExtractionBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _traceCollector = traceCollector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory extraction background service started");

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(30));

                await using var scope = _scopeFactory.CreateAsyncScope();
                var middleware = scope.ServiceProvider.GetRequiredService<ConversationMemoryMiddleware>();

                var storeStart = DateTimeOffset.UtcNow;
                using var memoryStoreActivity = AgentTelemetry.StartMemoryStore(item.UserId);
                await middleware.ExtractAndStoreAsync(item.UserId, item.UserMessage, item.AssistantReply, linkedCts.Token);
                var storeEnd = DateTimeOffset.UtcNow;

                if (item.TraceId is not null)
                {
                    _traceCollector.CaptureSpan(new TraceSpan(
                        SpanId: Guid.NewGuid().ToString("N")[..16],
                        TraceId: item.TraceId,
                        ParentSpanId: item.ParentSpanId,
                        OperationName: "memory.store",
                        StartTime: storeStart,
                        EndTime: storeEnd,
                        DurationMs: (storeEnd - storeStart).TotalMilliseconds,
                        Tags: new Dictionary<string, string>
                        {
                            ["memory.user_id"] = item.UserId,
                            ["memory.entries_stored"] = "extracted"
                        }));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background memory extraction failed for user {UserId}", item.UserId);
            }
        }

        _logger.LogInformation("Memory extraction background service stopped (dropped={DroppedCount})", _channel.DroppedCount);
    }
}
