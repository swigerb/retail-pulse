using System.Threading.Channels;

namespace RetailPulse.Api.Memory;

/// <summary>
/// Bounded channel for memory extraction work items.
/// Capacity: 1000. When full, new items are dropped and the dropped counter increments.
/// </summary>
public sealed class MemoryExtractionChannel
{
    private readonly Channel<MemoryWorkItem> _channel;
    private long _droppedCount;

    public const int DefaultCapacity = 1000;

    public MemoryExtractionChannel(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<MemoryWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelReader<MemoryWorkItem> Reader => _channel.Reader;

    /// <summary>Number of items dropped because the channel was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Tries to enqueue a work item. Returns false and increments the drop counter if the channel is full.
    /// </summary>
    public bool TryWrite(MemoryWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
