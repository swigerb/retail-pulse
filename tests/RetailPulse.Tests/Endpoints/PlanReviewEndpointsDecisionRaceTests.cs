using System.Text.Json;
using FluentAssertions;
using RetailPulse.Api.Endpoints;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Tests.Endpoints;

/// <summary>
/// Unit coverage for <see cref="PlanReviewEndpoints.ExtractWinner"/> — the
/// helper that resolves a decision's <c>kind</c>/<c>feedback</c> from the
/// persisted <see cref="ApprovalResult.ResponsePayload"/> so a losing
/// concurrent caller cannot broadcast its own requested kind. The endpoint
/// wires this into the plan_review_resolved payload; when the gate returns
/// a winner's payload with a different <c>Kind</c> than the caller
/// requested, the endpoint MUST advertise the winning kind, not the
/// caller's kind.
/// </summary>
public sealed class PlanReviewEndpointsDecisionRaceTests
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ExtractWinner_returns_the_persisted_kind_when_it_differs_from_the_caller_requested_kind()
    {
        // A concurrent race: caller asked to Approve, but the persisted
        // winner (an earlier reject) recorded Kind = "reject" with feedback.
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

        (string kind, string? feedback) = PlanReviewEndpoints.ExtractWinner(
            result, requestedKind: PlanReviewKinds.Approve, requestedFeedback: null);

        kind.Should().Be(PlanReviewKinds.Reject,
            "the SignalR broadcast MUST advertise the persisted winning kind, not the caller's kind.");
        feedback.Should().Be("the-persisted-feedback",
            "reviewer feedback on the row's ResponsePayload wins over the caller's own body.");
    }

    [Fact]
    public void ExtractWinner_falls_back_to_the_caller_requested_kind_when_payload_is_missing()
    {
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.TimedOut,
            Comment: null,
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "Timeout",
            ResponsePayload: null);

        (string kind, string? feedback) = PlanReviewEndpoints.ExtractWinner(
            result, requestedKind: PlanReviewKinds.Approve, requestedFeedback: null);

        kind.Should().Be(PlanReviewKinds.Approve,
            "a payload-less terminal row (timeout, orphaned) falls back to the caller-requested kind.");
        feedback.Should().BeNull();
    }

    [Fact]
    public void ExtractWinner_falls_back_to_the_caller_requested_kind_when_payload_is_malformed()
    {
        var result = new ApprovalResult(
            RequestId: "r1",
            Decision: ApprovalDecision.Approved,
            Comment: null,
            RespondedAt: DateTimeOffset.UtcNow,
            TerminalReason: "HumanApproved",
            ResponsePayload: "this is not valid JSON");

        (string kind, string? feedback) = PlanReviewEndpoints.ExtractWinner(
            result, requestedKind: PlanReviewKinds.Approve, requestedFeedback: "fallback");

        kind.Should().Be(PlanReviewKinds.Approve);
        feedback.Should().Be("fallback");
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

        (string kind, string? feedback) = PlanReviewEndpoints.ExtractWinner(
            result, requestedKind: PlanReviewKinds.Approve, requestedFeedback: null);

        kind.Should().Be(PlanReviewKinds.Edit);
        feedback.Should().BeNull("edit payloads carry EditedSteps rather than Feedback.");
    }
}
