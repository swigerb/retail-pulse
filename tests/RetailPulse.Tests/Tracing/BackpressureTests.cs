using System.Threading.Channels;
using FluentAssertions;
using RetailPulse.Contracts.Tracing;

namespace RetailPulse.Tests.Tracing;

/// <summary>
/// Sprint 3 reliability: trace collector backpressure via bounded channel.
/// Validates that traces within capacity are forwarded, overflow traces are
/// counted as dropped, and a hosted service pattern processes the queue.
/// </summary>
public class BackpressureTests
{
    private const int ChannelCapacity = 1000;

    private static TraceSpan MakeSpan(string traceId, string operationName = "test.op")
    {
        var start = DateTimeOffset.UtcNow;
        return new TraceSpan(
            SpanId: Guid.NewGuid().ToString("N"),
            TraceId: traceId,
            ParentSpanId: null,
            OperationName: operationName,
            StartTime: start,
            EndTime: start.AddMilliseconds(50),
            DurationMs: 50,
            InputTokens: 100,
            OutputTokens: 200);
    }

    /// <summary>
    /// Simulates the trace backpressure channel with dropped-item counter.
    /// Production code will use this pattern in a BackgroundService.
    /// Uses BoundedChannelFullMode.Wait + TryWrite for non-blocking drop detection.
    /// </summary>
    private sealed class TraceChannelCollector
    {
        private readonly Channel<TraceSpan> _channel;
        private long _droppedCount;

        public long DroppedCount => Interlocked.Read(ref _droppedCount);
        public ChannelReader<TraceSpan> Reader => _channel.Reader;
        public int Capacity { get; }

        public TraceChannelCollector(int capacity = ChannelCapacity)
        {
            Capacity = capacity;
            _channel = Channel.CreateBounded<TraceSpan>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
        }

        public bool TryEnqueue(TraceSpan span)
        {
            if (_channel.Writer.TryWrite(span))
                return true;

            Interlocked.Increment(ref _droppedCount);
            return false;
        }

        public void Complete() => _channel.Writer.Complete();
    }

    // ── Traces Within Capacity Are Forwarded ────────────────────────────

    [Fact]
    public async Task TracesWithinCapacity_AreForwarded()
    {
        var collector = new TraceChannelCollector(capacity: 100);
        var forwarded = new List<TraceSpan>();

        // Enqueue 50 traces (well within capacity)
        for (int i = 0; i < 50; i++)
            collector.TryEnqueue(MakeSpan($"trace-{i}")).Should().BeTrue();

        collector.Complete();

        await foreach (var span in collector.Reader.ReadAllAsync())
            forwarded.Add(span);

        forwarded.Should().HaveCount(50);
        collector.DroppedCount.Should().Be(0);
    }

    [Fact]
    public async Task TracesAtExactCapacity_AllForwarded()
    {
        const int capacity = 20;
        var collector = new TraceChannelCollector(capacity);

        for (int i = 0; i < capacity; i++)
            collector.TryEnqueue(MakeSpan($"trace-{i}")).Should().BeTrue();

        collector.Complete();

        var forwarded = new List<TraceSpan>();
        await foreach (var span in collector.Reader.ReadAllAsync())
            forwarded.Add(span);

        forwarded.Should().HaveCount(capacity);
        collector.DroppedCount.Should().Be(0);
    }

    // ── Overflow Traces Counted as Dropped ──────────────────────────────

    [Fact]
    public void OverflowTraces_AreCountedAsDropped()
    {
        const int capacity = 10;
        var collector = new TraceChannelCollector(capacity);

        // Fill to capacity
        for (int i = 0; i < capacity; i++)
            collector.TryEnqueue(MakeSpan($"trace-{i}"));

        // Overflow
        for (int i = 0; i < 5; i++)
            collector.TryEnqueue(MakeSpan($"overflow-{i}")).Should().BeFalse();

        collector.DroppedCount.Should().Be(5);
    }

    [Fact]
    public void DroppedCount_AccumulatesAcrossMultipleBursts()
    {
        const int capacity = 5;
        var collector = new TraceChannelCollector(capacity);

        // Fill
        for (int i = 0; i < capacity; i++)
            collector.TryEnqueue(MakeSpan($"trace-{i}"));

        // First burst of drops
        for (int i = 0; i < 3; i++)
            collector.TryEnqueue(MakeSpan($"drop1-{i}"));

        collector.DroppedCount.Should().Be(3);

        // Second burst of drops
        for (int i = 0; i < 4; i++)
            collector.TryEnqueue(MakeSpan($"drop2-{i}"));

        collector.DroppedCount.Should().Be(7, "dropped count accumulates");
    }

    [Fact]
    public async Task DroppedCount_ThreadSafe_UnderConcurrentOverflow()
    {
        const int capacity = 5;
        var collector = new TraceChannelCollector(capacity);

        // Fill to capacity
        for (int i = 0; i < capacity; i++)
            collector.TryEnqueue(MakeSpan($"fill-{i}"));

        // Concurrent overflow writes
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            collector.TryEnqueue(MakeSpan($"concurrent-{i}"));
        }));

        await Task.WhenAll(tasks);

        collector.DroppedCount.Should().Be(100, "all concurrent overflows counted");
    }

    // ── Hosted Service Processes Queue ───────────────────────────────────

    [Fact]
    public async Task HostedService_ProcessesAllEnqueuedTraces()
    {
        var collector = new TraceChannelCollector(capacity: 100);
        var processed = new List<string>();

        // Simulate hosted service consumer
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var span in collector.Reader.ReadAllAsync())
                processed.Add(span.TraceId);
        });

        // Producer enqueues traces
        for (int i = 0; i < 30; i++)
            collector.TryEnqueue(MakeSpan($"hosted-{i}"));

        collector.Complete();
        await consumerTask;

        processed.Should().HaveCount(30);
    }

    [Fact]
    public async Task HostedService_StopsOnCancellation()
    {
        var collector = new TraceChannelCollector(capacity: 100);
        using var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Enqueue items
        for (int i = 0; i < 50; i++)
            collector.TryEnqueue(MakeSpan($"cancel-{i}"));

        // Consumer with cancellation
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var span in collector.Reader.ReadAllAsync(cts.Token))
                {
                    var count = Interlocked.Increment(ref processedCount);
                    if (count >= 10)
                    {
                        cts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected — graceful shutdown
            }
        });

        await consumerTask;

        processedCount.Should().BeGreaterThanOrEqualTo(10);
        processedCount.Should().BeLessThanOrEqualTo(50, "processing should eventually stop");
    }

    [Fact]
    public async Task HostedService_ProcessesInFifoOrder()
    {
        var collector = new TraceChannelCollector(capacity: 100);
        var order = new List<string>();

        var consumerTask = Task.Run(async () =>
        {
            await foreach (var span in collector.Reader.ReadAllAsync())
                order.Add(span.TraceId);
        });

        for (int i = 0; i < 20; i++)
            collector.TryEnqueue(MakeSpan($"order-{i:D3}"));

        collector.Complete();
        await consumerTask;

        order.Should().BeInAscendingOrder("channel preserves FIFO order");
    }
}
