using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;
using RetailPulse.Contracts.Guardrails;

namespace RetailPulse.Tests.Guardrails.ContentSafety;

/// <summary>
/// A5 — tool-result moderation via the non-Agents seam. The inspector must
/// short-circuit on the disabled path and, when enabled, replace a blocked
/// tool payload with a diagnostic envelope while writing an audit row.
/// </summary>
public class ContentSafetyToolResultTests
{
    [Fact]
    public async Task Inspector_Disabled_ReturnsOriginalPayload()
    {
        var fake = new FakeContentSafetyEvaluator();
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig { Enabled = false }
        };
        var inspector = new ContentSafetyToolResultInspector(
            fake, new InMemorySuspiciousRequestLog(), config,
            NullLoggerFactory.Instance.CreateLogger<ContentSafetyToolResultInspector>());

        ContentSafetyToolResultOutcome outcome = await inspector.InspectAsync(
            "GetNorthwestRevenue", /*lang=json,strict*/ "{\"revenue\":123456}", "user-1", CancellationToken.None);

        outcome.WasBlocked.Should().BeFalse();
        outcome.Payload.Should().Be(/*lang=json,strict*/ "{\"revenue\":123456}");
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspector_Blocked_ReplacesPayloadWithEnvelopeAndAudits()
    {
        var fake = new FakeContentSafetyEvaluator();
        fake.Enqueue(ContentSafetyStage.ToolResult, new ContentSafetyResult(
            ContentSafetyDecision.Blocked,
            [new ContentSafetyCategoryHit("Violence", 6)],
            PromptShieldJailbreakDetected: false,
            PromptShieldIndirectInjectionDetected: false,
            Latency: TimeSpan.FromMilliseconds(12),
            CorrelationId: null,
            PrimaryCategory: ContentSafetyDetectionTypes.Violence));

        var log = new InMemorySuspiciousRequestLog();
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = "https://example.cognitiveservices.azure.com",
                CheckToolResults = true
            }
        };
        var inspector = new ContentSafetyToolResultInspector(
            fake, log, config,
            NullLoggerFactory.Instance.CreateLogger<ContentSafetyToolResultInspector>());

        ContentSafetyToolResultOutcome outcome = await inspector.InspectAsync(
            "GetIntel", /*lang=json,strict*/ "{\"payload\":\"blocked content\"}", "user-99", CancellationToken.None);

        outcome.WasBlocked.Should().BeTrue();
        outcome.Payload.Should().NotContain("blocked content");
        outcome.Payload.Should().Contain("_content_safety");
        outcome.Payload.Should().Contain("Violence");
        outcome.Payload.Should().Contain("severe severity");
        outcome.Payload.Should().Contain("withheld");
        (await log.GetRecentAsync(10)).Should().Contain(r =>
            r.DetectionType == ContentSafetyDetectionTypes.Violence
            && r.Action == ContentSafetyActions.Blocked);
    }

    [Fact]
    public async Task Inspector_Unavailable_FailClosed_ReplacesPayload()
    {
        var fake = new FakeContentSafetyEvaluator();
        fake.Enqueue(ContentSafetyStage.ToolResult, ContentSafetyResult.ServiceUnavailable);

        var log = new InMemorySuspiciousRequestLog();
        var config = new GuardrailsConfig
        {
            ContentSafety = new ContentSafetyConfig
            {
                Enabled = true,
                Endpoint = "https://example.cognitiveservices.azure.com",
                CheckToolResults = true,
                OnUnavailable = ContentSafetyFailPolicy.FailClosed
            }
        };
        var inspector = new ContentSafetyToolResultInspector(
            fake, log, config,
            NullLoggerFactory.Instance.CreateLogger<ContentSafetyToolResultInspector>());

        ContentSafetyToolResultOutcome outcome = await inspector.InspectAsync(
            "GetIntel", /*lang=json,strict*/ "{\"payload\":\"a\"}", "user-2", CancellationToken.None);

        outcome.WasBlocked.Should().BeTrue();
        outcome.DetectionType.Should().Be(ContentSafetyDetectionTypes.Unavailable);
        (await log.GetRecentAsync(10)).Should().Contain(r =>
            r.DetectionType == ContentSafetyDetectionTypes.Unavailable
            && r.Action == ContentSafetyActions.FailClosedBlocked);
    }
}
