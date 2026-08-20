using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;
using Xunit.Abstractions;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection finding #7 — the revision must ship measured P50/P95 numbers.
/// A live Content Safety resource is NOT required. This harness drives the
/// middleware against a deterministic local fake evaluator with a small,
/// stable artificial delay so the measurement reflects the middleware and
/// telemetry envelope, not network jitter.
///
/// <b>Methodology:</b> N warmup + M measured calls to
/// <c>GuardrailsMiddleware.CheckInputAsync</c>; per-call wall-clock is
/// captured with a high-resolution stopwatch; percentiles are computed with
/// nearest-rank on the sorted sample. The delay in the fake evaluator is
/// documented and constant so the number is comparable across runs.
/// </summary>
public class ContentSafetyLatencyHarnessTests
{
    private readonly ITestOutputHelper _output;

    public ContentSafetyLatencyHarnessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Middleware_LocalFake_P95_UnderSmokeCeiling()
    {
        const int warmup = 50;
        const int samples = 200;
        var fake = new FakeContentSafetyEvaluator
        {
            // 2 ms deterministic delay simulates a fast local evaluator so
            // the overhead we measure is the middleware envelope, not I/O.
            Matcher = (_, _) =>
            {
                Thread.Sleep(2);
                return ContentSafetyResult.Passed;
            }
        };
        GuardrailsMiddleware mw = BuildMiddleware(fake);

        for (int i = 0; i < warmup; i++)
        {
            _ = await mw.CheckInputAsync(new ChatRequest("warmup message", "s"));
        }

        var timings = new double[samples];
        var sw = new Stopwatch();
        for (int i = 0; i < samples; i++)
        {
            sw.Restart();
            _ = await mw.CheckInputAsync(new ChatRequest($"benign message {i}", "s"));
            sw.Stop();
            timings[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        double p50 = Percentile(timings, 50);
        double p95 = Percentile(timings, 95);
        double p99 = Percentile(timings, 99);

        _output.WriteLine($"ContentSafety local-fake harness: n={samples}, delay=2ms");
        _output.WriteLine($"P50={p50:F2}ms  P95={p95:F2}ms  P99={p99:F2}ms  min={timings[0]:F2}ms  max={timings[^1]:F2}ms");

        // Smoke ceiling: with a 2ms fake and full middleware envelope, P95
        // should sit well under 100ms on any dev/CI machine. Anything above
        // is a regression in the middleware envelope, not the fake.
        p95.Should().BeLessThan(100,
            "middleware envelope + 2ms fake evaluator should stay well under 100ms P95 even on slow CI hardware");
    }

    private static double Percentile(double[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0)
        {
            return 0d;
        }
        int rank = (int)Math.Ceiling(percentile / 100.0 * sortedAscending.Length);
        rank = Math.Clamp(rank, 1, sortedAscending.Length);
        return sortedAscending[rank - 1];
    }

    private static GuardrailsMiddleware BuildMiddleware(IContentSafetyEvaluator evaluator)
    {
        var log = new InMemorySuspiciousRequestLog();
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(t => t.GetTenant()).Returns(new TenantConfiguration());
        var logger = new Mock<ILogger<GuardrailsMiddleware>>();
        var config = new GuardrailsConfig
        {
            PiiDetectionEnabled = false,
            AutoRedactPii = false,
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = "https://example.cognitiveservices.azure.com",
                CheckInput = true,
                PromptShieldsEnabled = false,
            }
        };
        return new GuardrailsMiddleware(config, log, tenantProvider.Object, logger.Object, evaluator);
    }
}
