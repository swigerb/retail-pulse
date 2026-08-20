using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection finding #12 — the process-global
/// <see cref="ContentSafetyToolResultAmbient"/> must be immutable/idempotent so
/// startup races and test-reset races are loud rather than silent. Ambient
/// state is kept only because issue #89 owns the AgentExecutionPipeline
/// constructor; ADR-010 documents that as the temporary boundary.
/// </summary>
public class ContentSafetyAmbientTests : IDisposable
{
    public ContentSafetyAmbientTests()
    {
        ContentSafetyToolResultAmbient.Reset();
    }

    public void Dispose() => ContentSafetyToolResultAmbient.Reset();

    [Fact]
    public void Install_SameInstanceTwice_IsIdempotent()
    {
        ContentSafetyToolResultInspector inspector = MakeInspector();

        ContentSafetyToolResultAmbient.Install(inspector);
        ContentSafetyToolResultAmbient.Install(inspector);

        ContentSafetyToolResultAmbient.Current.Should().BeSameAs(inspector);
    }

    [Fact]
    public void Install_DifferentInstance_Throws()
    {
        ContentSafetyToolResultAmbient.Install(MakeInspector());

        Action second = () => ContentSafetyToolResultAmbient.Install(MakeInspector());

        second.Should().Throw<InvalidOperationException>(
            "a second inspector installed from a different startup path signals a wiring race and must fail loudly");
    }

    [Fact]
    public async Task Install_ConcurrentCallers_ExactlyOneInspectorWins()
    {
        ContentSafetyToolResultInspector[] inspectors =
            [.. Enumerable.Range(0, 16).Select(_ => MakeInspector())];

        int successCount = 0;
        int failureCount = 0;

        Task[] tasks = [.. inspectors.Select(i => Task.Run(() =>
        {
            try
            {
                ContentSafetyToolResultAmbient.Install(i);
                Interlocked.Increment(ref successCount);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref failureCount);
            }
        }))];

        await Task.WhenAll(tasks);

        successCount.Should().Be(1,
            "exactly one Install must win; the rest must throw so the race is observable");
        failureCount.Should().Be(inspectors.Length - 1);
        ContentSafetyToolResultAmbient.Current.Should().NotBeNull();
        inspectors.Should().Contain(i => ReferenceEquals(i, ContentSafetyToolResultAmbient.Current));
    }

    private static ContentSafetyToolResultInspector MakeInspector() =>
        new(
            NoOpContentSafetyEvaluator.Instance,
            new InMemorySuspiciousRequestLog(),
            new GuardrailsConfig(),
            NullLogger<ContentSafetyToolResultInspector>.Instance);
}
