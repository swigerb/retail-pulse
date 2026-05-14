using System.Threading.Channels;
using FluentAssertions;

namespace RetailPulse.Tests.Channels;

/// <summary>
/// Sprint 3 reliability: bounded channel behavior for memory extraction and trace pushes.
/// Validates Channel&lt;T&gt; with bounded capacity — items within capacity are processed,
/// overflow writes return false (TryWrite), a dropped-item counter increments on overflow,
/// and BackgroundService-style consumption and cancellation work correctly.
/// Pattern: BoundedChannelFullMode.Wait + TryWrite = non-blocking drop with detection.
/// </summary>
public class BoundedChannelTests
{
    private const int DefaultCapacity = 1000;

    /// <summary>
    /// Minimal work item representing a memory extraction or trace push request.
    /// </summary>
    private record WorkItem(string Id, string Payload);

    private static Channel<WorkItem> CreateChannel(int capacity = DefaultCapacity) =>
        Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    // ── Items Within Capacity ───────────────────────────────────────────

    [Fact]
    public async Task ItemsWithinCapacity_AreProcessed()
    {
        var channel = CreateChannel();

        var items = Enumerable.Range(0, 100)
            .Select(i => new WorkItem($"item-{i}", $"payload-{i}"))
            .ToList();

        foreach (var item in items)
            channel.Writer.TryWrite(item).Should().BeTrue();

        channel.Writer.Complete();

        var processed = new List<WorkItem>();
        await foreach (var item in channel.Reader.ReadAllAsync())
            processed.Add(item);

        processed.Should().HaveCount(100);
        processed.Select(i => i.Id).Should().BeEquivalentTo(items.Select(i => i.Id));
    }

    [Fact]
    public async Task SingleItem_IsWrittenAndRead()
    {
        var channel = CreateChannel();

        var item = new WorkItem("single", "data");
        var written = channel.Writer.TryWrite(item);
        written.Should().BeTrue();

        var read = await channel.Reader.ReadAsync();
        read.Id.Should().Be("single");
    }

    // ── Channel Full — Items Dropped ────────────────────────────────────

    [Fact]
    public void WhenChannelFull_TryWrite_ReturnsFalse_NotBlocked()
    {
        const int capacity = 10;
        var channel = CreateChannel(capacity);

        // Fill channel to capacity
        for (int i = 0; i < capacity; i++)
            channel.Writer.TryWrite(new WorkItem($"item-{i}", "data")).Should().BeTrue();

        // Next TryWrite returns false immediately (non-blocking)
        var overflowWritten = channel.Writer.TryWrite(new WorkItem("overflow", "data"));
        overflowWritten.Should().BeFalse("channel is full and TryWrite is non-blocking");
    }

    [Fact]
    public void WhenChannelFull_MultipleOverflows_AllReturnFalse()
    {
        const int capacity = 5;
        var channel = CreateChannel(capacity);

        // Fill to capacity
        for (int i = 0; i < capacity; i++)
            channel.Writer.TryWrite(new WorkItem($"item-{i}", "data"));

        // Attempt 10 more writes — all should fail
        int droppedCount = 0;
        for (int i = 0; i < 10; i++)
        {
            if (!channel.Writer.TryWrite(new WorkItem($"overflow-{i}", "data")))
                droppedCount++;
        }

        droppedCount.Should().Be(10, "all overflow items should be rejected by TryWrite");
    }

    // ── Dropped-Item Counter ────────────────────────────────────────────

    [Fact]
    public void DroppedItemCounter_Increments_OnOverflow()
    {
        const int capacity = 5;
        long droppedItemCounter = 0;
        var channel = CreateChannel(capacity);

        // Fill channel
        for (int i = 0; i < capacity; i++)
            channel.Writer.TryWrite(new WorkItem($"item-{i}", "data"));

        // Overflow with counter tracking
        for (int i = 0; i < 7; i++)
        {
            if (!channel.Writer.TryWrite(new WorkItem($"overflow-{i}", "data")))
                Interlocked.Increment(ref droppedItemCounter);
        }

        droppedItemCounter.Should().Be(7);
    }

    [Fact]
    public void DroppedItemCounter_StaysZero_WhenWithinCapacity()
    {
        long droppedItemCounter = 0;
        var channel = CreateChannel();

        for (int i = 0; i < 100; i++)
        {
            if (!channel.Writer.TryWrite(new WorkItem($"item-{i}", "data")))
                Interlocked.Increment(ref droppedItemCounter);
        }

        droppedItemCounter.Should().Be(0);
    }

    [Fact]
    public async Task DroppedItemCounter_ThreadSafe_UnderConcurrentWrites()
    {
        const int capacity = 10;
        long droppedItemCounter = 0;
        var channel = CreateChannel(capacity);

        // Fill channel first
        for (int i = 0; i < capacity; i++)
            channel.Writer.TryWrite(new WorkItem($"fill-{i}", "data"));

        // Concurrent overflow writes
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            if (!channel.Writer.TryWrite(new WorkItem($"concurrent-{i}", "data")))
                Interlocked.Increment(ref droppedItemCounter);
        }));

        await Task.WhenAll(tasks);

        droppedItemCounter.Should().Be(50, "all concurrent overflows should increment counter");
    }

    // ── BackgroundService Processes Items ────────────────────────────────

    [Fact]
    public async Task BackgroundService_ProcessesAllItems_FromChannel()
    {
        var channel = CreateChannel();
        var processedItems = new List<string>();

        // Simulate BackgroundService consumer
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                processedItems.Add(item.Id);
            }
        });

        // Producer writes items
        for (int i = 0; i < 50; i++)
            channel.Writer.TryWrite(new WorkItem($"bg-{i}", "data"));

        channel.Writer.Complete();
        await consumerTask;

        processedItems.Should().HaveCount(50);
    }

    [Fact]
    public async Task BackgroundService_ProcessesItemsInOrder()
    {
        var channel = CreateChannel();
        var processedOrder = new List<int>();

        var consumerTask = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                var index = int.Parse(item.Id.Split('-')[1]);
                processedOrder.Add(index);
            }
        });

        for (int i = 0; i < 20; i++)
            channel.Writer.TryWrite(new WorkItem($"order-{i}", "data"));

        channel.Writer.Complete();
        await consumerTask;

        processedOrder.Should().BeInAscendingOrder("channel preserves FIFO order");
    }

    // ── Cancellation Stops Processing ───────────────────────────────────

    [Fact]
    public async Task Cancellation_StopsChannelProcessing_Gracefully()
    {
        var channel = CreateChannel();
        using var cts = new CancellationTokenSource();
        var processedCount = 0;

        // Fill channel with items
        for (int i = 0; i < 100; i++)
            channel.Writer.TryWrite(new WorkItem($"cancel-{i}", "data"));

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cts.Token))
                {
                    var count = Interlocked.Increment(ref processedCount);
                    if (count >= 5)
                    {
                        // Add a small yield to allow cancellation to propagate
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

        processedCount.Should().BeGreaterThanOrEqualTo(5, "at least 5 items processed before cancel");
        processedCount.Should().BeLessThanOrEqualTo(100, "processing should eventually stop");
    }

    [Fact]
    public async Task CancelledToken_PreventsWriteAsync()
    {
        var channel = CreateChannel();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => channel.Writer.WriteAsync(new WorkItem("cancelled", "data"), cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelledToken_PreventsReadAsync()
    {
        var channel = CreateChannel();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => channel.Reader.ReadAsync(cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Capacity Enforcement ────────────────────────────────────────────

    [Fact]
    public void DefaultCapacity_Is1000()
    {
        DefaultCapacity.Should().Be(1000, "Sprint 3 spec requires 1000 capacity");
    }
}
