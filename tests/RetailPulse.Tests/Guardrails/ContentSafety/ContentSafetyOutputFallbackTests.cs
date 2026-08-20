using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Api.Middleware;
using RetailPulse.Contracts;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// Rejection finding #10 — an output-side block that lacks a
/// <see cref="ContentSafetyResult.PrimaryCategory"/> must NEVER be labeled
/// <c>content-safety-prompt-shield</c> because the output stage does not run
/// Prompt Shields. Regression guard for the previous
/// <c>?? ContentSafetyDetectionTypes.PromptShield</c> fallback.
/// </summary>
public class ContentSafetyOutputFallbackTests
{
    [Fact]
    public async Task Output_BlockedWithoutCategory_UsesGenericBlock_NotPromptShield()
    {
        (GuardrailsMiddleware mw, InMemorySuspiciousRequestLog log, FakeContentSafetyEvaluator fake) = Build();
        // A Blocked decision with NO categories and NO PrimaryCategory — the
        // pathological shape the pre-fix middleware would have mislabeled.
        fake.Enqueue(ContentSafetyStage.Output, new ContentSafetyResult(
            ContentSafetyDecision.Blocked,
            [],
            PromptShieldJailbreakDetected: false,
            PromptShieldIndirectInjectionDetected: false,
            Latency: TimeSpan.FromMilliseconds(5),
            CorrelationId: null,
            PrimaryCategory: null));

        _ = await mw.FilterOutputAsync("harmless response", "u", CancellationToken.None);

        IReadOnlyList<SuspiciousRequest> rows = await log.GetRecentAsync(10);
        SuspiciousRequest row = rows.Should().ContainSingle().Subject;
        row.DetectionType.Should().Be(ContentSafetyDetectionTypes.Block,
            "Prompt Shields does not run on the output stage — a categoryless output block must fall back to a generic content-safety block, not prompt-shield");
        row.DetectionType.Should().NotBe(ContentSafetyDetectionTypes.PromptShield);
    }

    [Fact]
    public async Task Output_BlockedWithCategoryOnly_UsesForCategoryMapping()
    {
        (GuardrailsMiddleware mw, InMemorySuspiciousRequestLog log, FakeContentSafetyEvaluator fake) = Build();
        fake.Enqueue(ContentSafetyStage.Output, new ContentSafetyResult(
            ContentSafetyDecision.Blocked,
            [new ContentSafetyCategoryHit("Violence", 6)],
            PromptShieldJailbreakDetected: false,
            PromptShieldIndirectInjectionDetected: false,
            Latency: TimeSpan.FromMilliseconds(5),
            CorrelationId: null,
            PrimaryCategory: null));

        _ = await mw.FilterOutputAsync("violent response", "u", CancellationToken.None);

        SuspiciousRequest row = (await log.GetRecentAsync(10)).Should().ContainSingle().Subject;
        row.DetectionType.Should().Be(ContentSafetyDetectionTypes.Violence);
    }

    private static (GuardrailsMiddleware mw, InMemorySuspiciousRequestLog log, FakeContentSafetyEvaluator fake) Build()
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
                CheckOutput = true,
            }
        };
        var fake = new FakeContentSafetyEvaluator();
        return (new GuardrailsMiddleware(config, log, tenantProvider.Object, logger.Object, contentSafety: fake), log, fake);
    }
}
