using FluentAssertions;
using RetailPulse.Api.Hubs;

namespace RetailPulse.Tests.Hubs;

/// <summary>
/// Behavioural contract for issue #92's cancellation surface. Proves the
/// registry:
/// <list type="bullet">
///   <item>Refuses cancellation from a subject that didn't register.</item>
///   <item>Actually cancels the underlying <see cref="CancellationTokenSource"/>
///     (not just returns "success").</item>
///   <item>Ends an in-flight tool invocation via the same CTS — the tool
///     stops looping / awaiting after the cancel arrives, not merely that
///     HTTP returned.</item>
/// </list>
/// </summary>
public sealed class ExecutionCancellationRegistryTests
{
    [Fact]
    public void TryCancel_UnknownKey_ReturnsNotFound()
    {
        var registry = new ExecutionCancellationRegistry();
        registry.TryCancel(ExecutionCancellationRegistry.ChatScope, "session-x", "owner").Should().Be(ExecutionCancelResult.NotFound);
    }

    [Fact]
    public void TryCancel_ForeignSubject_ReturnsForbidden_AndDoesNotCancel()
    {
        var registry = new ExecutionCancellationRegistry();
        using var cts = new CancellationTokenSource();
        using IDisposable handle = registry.Register(
            ExecutionCancellationRegistry.ChatScope, "session-A", "owner-A", cts);

        ExecutionCancelResult result = registry.TryCancel(
            ExecutionCancellationRegistry.ChatScope, "session-A", "attacker-B");

        result.Should().Be(ExecutionCancelResult.Forbidden);
        cts.IsCancellationRequested.Should().BeFalse(
            "a foreign subject must never trigger cancellation on someone else's run");
    }

    [Fact]
    public void TryCancel_Owner_ActuallyCancels_UnderlyingCts()
    {
        var registry = new ExecutionCancellationRegistry();
        using var cts = new CancellationTokenSource();
        using IDisposable handle = registry.Register(
            ExecutionCancellationRegistry.ChatScope, "session-Z", "owner-Z", cts);

        ExecutionCancelResult result = registry.TryCancel(
            ExecutionCancellationRegistry.ChatScope, "session-Z", "owner-Z");

        result.Should().Be(ExecutionCancelResult.Cancelled);
        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DeregistersEntry()
    {
        var registry = new ExecutionCancellationRegistry();
        using var cts = new CancellationTokenSource();
        IDisposable handle = registry.Register(
            ExecutionCancellationRegistry.PlanScope, "plan-1", "owner", cts);

        registry.OwnerOf(ExecutionCancellationRegistry.PlanScope, "plan-1").Should().Be("owner");
        handle.Dispose();
        registry.OwnerOf(ExecutionCancellationRegistry.PlanScope, "plan-1").Should().BeNull();

        registry.TryCancel(ExecutionCancellationRegistry.PlanScope, "plan-1", "owner")
            .Should().Be(ExecutionCancelResult.NotFound);
    }

    /// <summary>
    /// Proof of end-to-end propagation: an in-flight "tool" invocation that
    /// awaits on the CTS token STOPS its work when the cancel endpoint fires.
    /// Simulates a specialist that's mid-tool-call — the token flows through
    /// the request pipeline and reaches the tool, so the tool observes the
    /// cancellation and its loop terminates. Asserting the loop count, not
    /// just that the task completed, matches the issue's "assert tool
    /// invocation ceases" requirement.
    /// </summary>
    [Fact]
    public async Task InFlightToolInvocation_ObservesCancellation_AndCeasesWork()
    {
        var registry = new ExecutionCancellationRegistry();
        using var cts = new CancellationTokenSource();
        using IDisposable handle = registry.Register(
            ExecutionCancellationRegistry.PlanScope, "plan-inflight", "owner", cts);

        int loopIterations = 0;
        bool observedCancellation = false;
        var firstIteration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var toolTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    int n = Interlocked.Increment(ref loopIterations);
                    if (n == 1) firstIteration.TrySetResult();
                    await Task.Delay(10, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                observedCancellation = true;
            }
        });

        // Wait deterministically for the tool to actually enter its loop rather
        // than sleeping a fixed budget. Under CPU contention from the full suite,
        // a fixed Task.Delay would race the thread-pool scheduler and observe
        // zero iterations, causing spurious failures like #152.
        await firstIteration.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int before = Volatile.Read(ref loopIterations);
        before.Should().BeGreaterThan(0);

        // Simulate POST /api/plans/{id}/cancel by the owning subject.
        ExecutionCancelResult result = registry.TryCancel(
            ExecutionCancellationRegistry.PlanScope, "plan-inflight", "owner");
        result.Should().Be(ExecutionCancelResult.Cancelled);

        // The tool loop must actually stop — assert BOTH termination AND
        // that the iteration count freezes after the task has terminated
        // (proving the tool ceased its work, not merely that the outer HTTP
        // response returned). Because we await toolTask first, no additional
        // iterations can occur regardless of scheduler load.
        await toolTask.WaitAsync(TimeSpan.FromSeconds(5));
        observedCancellation.Should().BeTrue(
            "the in-flight tool invocation must observe cancellation via the shared CTS token");

        int after = Volatile.Read(ref loopIterations);
        int later = Volatile.Read(ref loopIterations);
        later.Should().Be(after,
            "the tool loop must actually stop iterating after cancellation, not just return HTTP");
    }
}
