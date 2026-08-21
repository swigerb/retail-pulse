using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Endpoints;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Unit coverage for <see cref="PlanReviewEndpoints.ExtractWinner"/> — the
/// helper that resolves a decision's <c>kind</c>/<c>feedback</c> from the
/// persisted <see cref="ApprovalResult"/> so a losing concurrent caller
/// cannot broadcast its own requested kind or feedback (#144 follow-up).
/// <para>
/// Integrity contract exercised here:
/// </para>
/// <list type="bullet">
///   <item><description><c>Kind</c> always follows the durable
///     <see cref="ApprovalResult.Decision"/> family, never the requesting
///     caller.</description></item>
///   <item><description>When the persisted
///     <see cref="ApprovalResult.ResponsePayload"/> parses cleanly, feedback
///     is taken from the persisted winner's payload only.</description></item>
///   <item><description>Missing or malformed payload returns
///     <c>PayloadParsed = false</c> and omits feedback so the endpoint can
///     skip the <c>plan_review_resolved</c> broadcast rather than
///     advertise a losing caller's text.</description></item>
/// </list>
/// </summary>
public sealed class PlanReviewEndpointsDecisionRaceTests
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ExtractWinner_returns_persisted_kind_and_feedback_when_payload_parses()
    {
        var persisted = new PlanReviewResponsePayload
        {
            Kind = PlanReviewKinds.Reject,
            Feedback = "the-persisted-feedback",
        };
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.Rejected,
            Comment: "the-persisted-feedback",
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "HumanRejected",
            ResponsePayload: JsonSerializer.Serialize(persisted, _json));

        (string kind, string? feedback, bool payloadParsed) = PlanReviewEndpoints.ExtractWinner(result);

        kind.Should().Be(PlanReviewKinds.Reject,
            "the SignalR broadcast MUST advertise the persisted winning kind.");
        feedback.Should().Be("the-persisted-feedback",
            "reviewer feedback on the row's ResponsePayload wins over any caller-side value.");
        payloadParsed.Should().BeTrue("a well-formed payload must clear the broadcast gate.");
    }

    [Fact]
    public void ExtractWinner_derives_kind_from_decision_and_omits_feedback_when_payload_missing()
    {
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.Rejected,
            Comment: null,
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "HumanRejected",
            ResponsePayload: null);

        (string kind, string? feedback, bool payloadParsed) = PlanReviewEndpoints.ExtractWinner(result);

        kind.Should().Be(PlanReviewKinds.Reject,
            "kind is derived from ApprovalResult.Decision, never the caller's requested kind.");
        feedback.Should().BeNull(
            "no payload means we cannot know the winning feedback — omit rather than leak the caller's text.");
        payloadParsed.Should().BeFalse(
            "the endpoint must observe PayloadParsed = false and skip the plan_review_resolved broadcast.");
    }

    [Fact]
    public void ExtractWinner_derives_kind_from_decision_and_omits_feedback_when_payload_malformed()
    {
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.Approved,
            Comment: null,
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "HumanApproved",
            ResponsePayload: "this is not valid JSON");

        (string kind, string? feedback, bool payloadParsed) = PlanReviewEndpoints.ExtractWinner(result);

        kind.Should().Be(PlanReviewKinds.Approve,
            "malformed payload still derives the kind from the durable Decision.");
        feedback.Should().BeNull(
            "malformed payload must not fall back to any caller-supplied string.");
        payloadParsed.Should().BeFalse();
    }

    [Fact]
    public void ExtractWinner_returns_edit_kind_when_persisted_payload_is_an_edit()
    {
        var persisted = new PlanReviewResponsePayload
        {
            Kind = PlanReviewKinds.Edit,
            EditedSteps =
            [
                new PlanReviewStepDto { SpecialistKey = "scorecard", Intent = "s", Action = "a" },
            ],
        };
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.Modified,
            Comment: null,
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "HumanModified",
            ResponsePayload: JsonSerializer.Serialize(persisted, _json));

        (string kind, string? feedback, bool payloadParsed) = PlanReviewEndpoints.ExtractWinner(result);

        kind.Should().Be(PlanReviewKinds.Edit);
        feedback.Should().BeNull("edit payloads carry EditedSteps rather than Feedback.");
        payloadParsed.Should().BeTrue();
    }

    [Fact]
    public void ExtractWinner_normalizes_timed_out_and_orphaned_decisions_to_reject_kind()
    {
        // Even without a payload, a timeout / orphan row must not present as
        // an approve on the broadcast surface. Kind normalizes to reject so
        // downstream consumers treat the row as a terminal reject.
        var timedOut = new ApprovalResult(
            "r1", ApprovalDecision.TimedOut, null, DateTimeOffset.UtcNow, "Timeout", null);
        var orphaned = new ApprovalResult(
            "r2", ApprovalDecision.Orphaned, null, DateTimeOffset.UtcNow, "Orphaned", null);

        PlanReviewEndpoints.ExtractWinner(timedOut).Kind.Should().Be(PlanReviewKinds.Reject);
        PlanReviewEndpoints.ExtractWinner(orphaned).Kind.Should().Be(PlanReviewKinds.Reject);
    }
}
