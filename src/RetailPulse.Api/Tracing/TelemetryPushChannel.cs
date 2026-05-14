using System.Threading.Channels;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Api.Tracing;

/// <summary>
/// Work item for SignalR telemetry push via bounded channel.
/// </summary>
public sealed record TelemetryPushItem(string EventType, TraceSpan? Span = null, string? TraceId = null, DateTimeOffset? Timestamp = null);

/// <summary>
/// Bounded channel for SignalR telemetry push work items.
/// Capacity: 1000. Drops writes when full and tracks dropped count.
/// </summary>
public sealed class TelemetryPushChannel
{
    private readonly Channel<TelemetryPushItem> _channel;
    private long _droppedCount;

    public const int DefaultCapacity = 1000;

    public TelemetryPushChannel(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<TelemetryPushItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelReader<TelemetryPushItem> Reader => _channel.Reader;

    /// <summary>Number of telemetry items dropped because the channel was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Tries to enqueue a telemetry push item. Returns false and increments the drop counter if full.
    /// </summary>
    public bool TryWrite(TelemetryPushItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
