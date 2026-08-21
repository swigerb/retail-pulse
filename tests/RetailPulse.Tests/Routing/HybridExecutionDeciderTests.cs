using FluentAssertions;
using RetailPulse.Api.Agents.Routing;
using RetailPulse.Api.Persistence;
using RetailPulse.Contracts.Routing;

namespace RetailPulse.Tests.Routing;

/// <summary>
/// Decision-table coverage for the hybrid execution decider (issue #95).
/// Every precedence rung in the design gate — explicit override, council
/// dedicated route, planner-unavailable short-circuit, multi-domain,
/// low-confidence, advisory phrase, single-domain default — is pinned as
/// a discrete case so future refactors can't silently reorder them.
/// </summary>
public sealed class HybridExecutionDeciderTests
{
    private static PlanPersistenceOptions DefaultOptions() => new()
    {
        MinDetectedIntentsForPlan = 2,
        MinConfidenceForFastPath = 0.6,
        AdvisoryPhrases = ["why did", "what should we", "recommend"],
    };

    private static RoutingDecision SingleHighConfidence(
        string intent = AgentIntent.DemandForecasting,
        double confidence = 0.92)
        => new("demand", intent, confidence, [intent]);

    // ── Precedence 1: explicit override ────────────────────────────────

    [Fact]
    public void Force_fast_wins_over_multi_domain_when_authenticated_and_planner_available()
    {
        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.9,
            [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "compare demand and supply for Q1",
            new HybridExecutionContext(
                AnonymousCaller: false,
                PlannerAvailable: true,
                ForcedPath: ExecutionPath.Fast),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Forced.Should().BeTrue();
        result.Reason.Should().Be(HybridExecutionReason.ForceOverride);
    }

    [Fact]
    public void Force_plan_wins_over_single_domain_when_authenticated_and_planner_available()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(
                AnonymousCaller: false,
                PlannerAvailable: true,
                ForcedPath: ExecutionPath.Plan),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Plan);
        result.Forced.Should().BeTrue();
        result.Reason.Should().Be(HybridExecutionReason.ForceOverride);
    }

    [Fact]
    public void Force_plan_is_ignored_when_planner_unavailable_and_falls_through_to_default_flow()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(
                AnonymousCaller: false,
                PlannerAvailable: false,
                ForcedPath: ExecutionPath.Plan),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Forced.Should().BeFalse();
        result.Reason.Should().Be(HybridExecutionReason.PlannerUnavailable);
    }

    [Fact]
    public void Force_override_is_ignored_for_anonymous_callers_and_falls_through()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(
                AnonymousCaller: true,
                PlannerAvailable: true,
                ForcedPath: ExecutionPath.Plan),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Forced.Should().BeFalse();
        result.Reason.Should().Be(HybridExecutionReason.AnonymousCaller);
    }

    // ── Precedence 2: council dedicated route ──────────────────────────

    [Fact]
    public void Council_intent_resolves_to_council_regardless_of_planner_availability()
    {
        var decision = new RoutingDecision(
            "council", AgentIntent.PortfolioHealth, 0.95, [AgentIntent.PortfolioHealth]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "how is the portfolio performing",
            new HybridExecutionContext(
                AnonymousCaller: false,
                PlannerAvailable: true,
                ForcedPath: null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Council);
        result.Forced.Should().BeFalse();
        result.Reason.Should().Be(HybridExecutionReason.CouncilIntent);
    }

    [Fact]
    public void Council_intent_detected_as_secondary_intent_still_resolves_to_council()
    {
        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.9,
            [AgentIntent.DemandForecasting, AgentIntent.PortfolioHealth]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "how are demand and portfolio doing",
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Council);
    }

    // ── Precedence 3: planner unavailable → fast ───────────────────────

    [Fact]
    public void Anonymous_caller_always_resolves_to_fast_when_council_intent_is_absent()
    {
        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.4,
            [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "why did demand drop and what should we do",
            new HybridExecutionContext(
                AnonymousCaller: true,
                PlannerAvailable: true,
                ForcedPath: null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Reason.Should().Be(HybridExecutionReason.AnonymousCaller);
    }

    [Fact]
    public void Planner_unavailable_resolves_to_fast_even_on_multi_domain_request()
    {
        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.9,
            [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "compare demand and supply",
            new HybridExecutionContext(false, PlannerAvailable: false, ForcedPath: null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Reason.Should().Be(HybridExecutionReason.PlannerUnavailable);
    }

    // ── Precedence 4: multi-domain → plan ──────────────────────────────

    [Fact]
    public void Detected_intents_at_or_above_threshold_resolve_to_plan()
    {
        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.85,
            [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "reconcile demand forecast and shipment plan",
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Plan);
        result.Reason.Should().Be(HybridExecutionReason.MultiDomain);
    }

    [Fact]
    public void Threshold_is_configurable_and_disables_multi_domain_admission_when_high()
    {
        PlanPersistenceOptions options = DefaultOptions();
        options.MinDetectedIntentsForPlan = 5;

        var decision = new RoutingDecision(
            "demand", AgentIntent.DemandForecasting, 0.9,
            [AgentIntent.DemandForecasting, AgentIntent.SupplyShipments]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "compare demand and supply",
            new HybridExecutionContext(false, true, null),
            options);

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Reason.Should().Be(HybridExecutionReason.SingleDomain);
    }

    // ── Precedence 5: low confidence → plan ────────────────────────────

    [Fact]
    public void Confidence_strictly_below_configured_floor_resolves_to_plan()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(confidence: 0.5),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Plan);
        result.Reason.Should().Be(HybridExecutionReason.LowConfidence);
    }

    [Fact]
    public void Confidence_at_configured_floor_stays_on_fast_path()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(confidence: 0.6),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Reason.Should().Be(HybridExecutionReason.SingleDomain);
    }

    // ── Precedence 6: advisory phrase → plan ───────────────────────────

    [Theory]
    [InlineData("Why did depletions drop in the Northeast?")]
    [InlineData("What should we do about the Southwest region?")]
    [InlineData("Recommend a promotion strategy for Q3")]
    public void Advisory_phrase_resolves_to_plan_even_on_single_high_confidence_intent(string message)
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            message,
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Plan);
        result.Reason.Should().Be(HybridExecutionReason.AdvisoryPhrase);
    }

    [Fact]
    public void Empty_advisory_phrase_list_disables_the_trigger()
    {
        PlanPersistenceOptions options = DefaultOptions();
        options.AdvisoryPhrases = [];

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            "Why did depletions drop?",
            new HybridExecutionContext(false, true, null),
            options);

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Reason.Should().Be(HybridExecutionReason.SingleDomain);
    }

    // ── Precedence 7: default fast ─────────────────────────────────────

    [Fact]
    public void Single_high_confidence_intent_resolves_to_fast_by_default()
    {
        HybridExecutionResult result = HybridExecutionDecider.Decide(
            SingleHighConfidence(),
            "How is Sierra Gold Tequila performing in the Northeast?",
            new HybridExecutionContext(false, true, null),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Forced.Should().BeFalse();
        result.Reason.Should().Be(HybridExecutionReason.SingleDomain);
    }

    // ── IsCouncilIntent helper ─────────────────────────────────────────

    [Fact]
    public void IsCouncilIntent_detects_council_intent_case_insensitively()
    {
        var decision = new RoutingDecision(
            "council", "COUNCIL/HEALTH", 0.9, ["Council/Health"]);

        HybridExecutionDecider.IsCouncilIntent(decision).Should().BeTrue();
    }

    [Fact]
    public void IsCouncilIntent_returns_false_for_non_council_decision() => HybridExecutionDecider.IsCouncilIntent(SingleHighConfidence()).Should().BeFalse();

    // ── Ordering — override outranks council when planner is available ─

    [Fact]
    public void Force_fast_overrides_council_intent_when_authenticated()
    {
        var decision = new RoutingDecision(
            "council", AgentIntent.PortfolioHealth, 0.95, [AgentIntent.PortfolioHealth]);

        HybridExecutionResult result = HybridExecutionDecider.Decide(
            decision,
            "how is the portfolio performing",
            new HybridExecutionContext(false, true, ExecutionPath.Fast),
            DefaultOptions());

        result.Path.Should().Be(ExecutionPath.Fast);
        result.Forced.Should().BeTrue();
        result.Reason.Should().Be(HybridExecutionReason.ForceOverride);
    }
}
