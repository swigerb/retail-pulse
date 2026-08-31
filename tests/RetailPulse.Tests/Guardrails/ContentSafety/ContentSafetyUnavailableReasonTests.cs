using FluentAssertions;
using RetailPulse.Api.Guardrails;
using RetailPulse.Api.Guardrails.ContentSafety;

namespace RetailPulse.Tests.Guardrails;

/// <summary>
/// The audit dashboard renders the fail-open Reason string verbatim. Before this
/// change every service-unavailable row read the same generic "unreachable"
/// sentence, so four cold-start rows looked identical and an operator could not
/// tell a timeout from a 401. These tests pin that each failure class now yields
/// distinct, operator-readable text while the unclassified case keeps the exact
/// original wording so existing audit consumers see no regression.
/// </summary>
public class ContentSafetyUnavailableReasonTests
{
    [Fact]
    public void UnclassifiedReason_KeepsOriginalWording()
    {
        string reason = GuardrailAuditFields.UnavailableReason(null, "the tool result");
        reason.Should().Be("Content Safety was unreachable while checking the tool result.");
    }

    [Theory]
    [InlineData(ContentSafetyFailureReason.Timeout, "timed out")]
    [InlineData(ContentSafetyFailureReason.Authentication, "authentication")]
    [InlineData(ContentSafetyFailureReason.Transport, "connection")]
    [InlineData(ContentSafetyFailureReason.CircuitOpen, "circuit breaker")]
    public void ClassifiedReason_NamesTheFailure_AndStaysUnreachable(
        ContentSafetyFailureReason reason, string expectedFragment)
    {
        string text = GuardrailAuditFields.UnavailableReason(reason, "the tool result");

        text.Should().Contain("unreachable",
            "the audit test suite and any counter keyed on 'unreachable' must keep matching");
        text.Should().Contain(expectedFragment,
            "each failure class must be distinguishable in the operator-visible reason");
    }

    [Fact]
    public void EveryFailureClass_ProducesDistinctText()
    {
        string[] variants =
        [
            GuardrailAuditFields.UnavailableReason(ContentSafetyFailureReason.Timeout, "x"),
            GuardrailAuditFields.UnavailableReason(ContentSafetyFailureReason.Authentication, "x"),
            GuardrailAuditFields.UnavailableReason(ContentSafetyFailureReason.Transport, "x"),
            GuardrailAuditFields.UnavailableReason(ContentSafetyFailureReason.CircuitOpen, "x"),
            GuardrailAuditFields.UnavailableReason(null, "x"),
        ];

        variants.Should().OnlyHaveUniqueItems(
            "an operator must be able to tell the failure classes apart at a glance");
    }
}
