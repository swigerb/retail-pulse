using FluentAssertions;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Api.Approval;
using RetailPulse.Contracts.Approval;
using RetailPulse.Contracts.Persistence;

namespace RetailPulse.Tests.Approval;

/// <summary>
/// Unit tests for <see cref="PlanAnswerComposer"/> — the composition helper
/// added for #301, where the plan-first final answer used to concatenate raw
/// specialist replies with a bare <c>---</c>. That produced two independently
/// written whole answers, each with its own "Bottom line" section and its own
/// conversational follow-up pitch, reading as though the assistant answered
/// twice and contradicted itself.
///
/// The composer now:
/// <list type="bullet">
///   <item>Attributes each multi-step contribution to its specialist and intent.</item>
///   <item>Strips trailing conversational follow-up offers ("If you want, I can
///     also…", "Let me know if…", etc.) without touching substantive body text.</item>
///   <item>Leaves single-step replies natural and unattributed.</item>
/// </list>
/// </summary>
public sealed class PlanAnswerComposerTests
{
    private static PlanAnswerComposer.StepContribution Step(string specialist, string intent, string result) =>
        new(specialist, intent, result);

    // ── #301 core: two conflicting whole answers must compose cleanly ───

    [Fact]
    public void Two_conflicting_whole_answers_are_composed_with_attribution_and_no_bare_rule()
    {
        // Reproduces the shape from #301 — two specialists each wrote a full
        // "Bottom line" answer with its own follow-up pitch and reached a
        // slightly different conclusion about "which region is better".
        const string forecastReply =
            "Historical demand favors the Northeast by ~8%.\n" +
            "\n" +
            "**Bottom line:** Northeast is the better bet.\n" +
            "\n" +
            "If you want, I can also turn this into a side-by-side scorecard.";

        const string scorecardReply =
            "On the 90-day forecast the Midwest edges ahead by ~3%.\n" +
            "\n" +
            "**Bottom line:** Midwest looks stronger going forward.\n" +
            "\n" +
            "If you want, I can next give you a clean Northeast vs Midwest scorecard with:\n" +
            "1. historical demand\n" +
            "2. 90-day forecast\n" +
            "3. margin outlook";

        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "compare historical demand", forecastReply),
            Step("scorecard", "score the 90-day forecast", scorecardReply),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().NotContain("\n---\n",
            "the bare `---` staple that produced the #301 bug must be gone.");
        composed.Should().Contain("## Step 1 — forecast: compare historical demand");
        composed.Should().Contain("## Step 2 — scorecard: score the 90-day forecast");

        // Substantive body content of both specialist replies is preserved.
        composed.Should().Contain("Historical demand favors the Northeast");
        composed.Should().Contain("Midwest edges ahead");

        // Both "Bottom line" lines survive — the composer must not delete
        // substantive content just because two answers disagree; the
        // attribution headings put each conclusion in its author's voice so
        // the reader sees two evidence bases, not one contradicting reply.
        composed.Should().Contain("**Bottom line:** Northeast");
        composed.Should().Contain("**Bottom line:** Midwest");

        // Both conversational follow-up pitches are stripped.
        composed.Should().NotContain("If you want, I can also turn this into a side-by-side scorecard");
        composed.Should().NotContain("If you want, I can next give you a clean Northeast vs Midwest scorecard");
        composed.Should().NotContain("1. historical demand",
            "the enumerated continuation of the follow-up offer must be stripped with it.");
    }

    // ── Single-step: natural, unattributed ──────────────────────────────

    [Fact]
    public void Single_non_empty_step_is_returned_verbatim_without_attribution_header()
    {
        const string reply = "Northeast wins on every metric this quarter.";

        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "single-shot answer", reply),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().Be(reply,
            "a single-specialist reply must read as one natural answer, not a report of one.");
        composed.Should().NotContain("## Step");
    }

    [Fact]
    public void Single_step_still_strips_its_own_trailing_offer()
    {
        const string reply =
            "Northeast wins on every metric this quarter.\n" +
            "\n" +
            "If you'd like, I can dig into per-store detail next.";

        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "single-shot answer", reply),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().Be("Northeast wins on every metric this quarter.");
    }

    // ── Markdown content preservation ───────────────────────────────────

    [Fact]
    public void Markdown_body_content_is_preserved_across_composition()
    {
        const string mdA =
            "### Findings\n" +
            "\n" +
            "| Region | Units |\n" +
            "| --- | --- |\n" +
            "| NE | 1,240 |\n" +
            "| MW | 1,190 |\n" +
            "\n" +
            "- Northeast leads on units.\n" +
            "- Margin gap is narrowing.\n" +
            "\n" +
            "Let me know if you want a deeper cut.";

        const string mdB =
            "```json\n" +
            /*lang=json,strict*/
                                 "{ \"region\": \"midwest\", \"growth\": 0.031 }\n" +
            "```\n" +
            "\n" +
            "**Note:** growth is quarter-over-quarter.\n" +
            "\n" +
            "Happy to add YoY next.";

        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "regional deep-dive", mdA),
            Step("scorecard", "growth check", mdB),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        // Markdown fences, tables, and lists survive intact.
        composed.Should().Contain("| Region | Units |");
        composed.Should().Contain("| NE | 1,240 |");
        composed.Should().Contain("- Northeast leads on units.");
        composed.Should().Contain("```json");
        composed.Should().Contain(/*lang=json,strict*/ "{ \"region\": \"midwest\", \"growth\": 0.031 }");
        composed.Should().Contain("**Note:** growth is quarter-over-quarter.");

        // Trailing conversational offers are stripped even when the body is
        // heavy markdown.
        composed.Should().NotContain("Let me know if you want a deeper cut");
        composed.Should().NotContain("Happy to add YoY next");

        // Attribution headings landed at the top of each step's block.
        composed.Should().Contain("## Step 1 — forecast: regional deep-dive");
        composed.Should().Contain("## Step 2 — scorecard: growth check");
    }

    // ── Offer-stripping variations ──────────────────────────────────────

    [Theory]
    [InlineData("If you want, I can also drill in.")]
    [InlineData("If you'd like, I can pull a chart.")]
    [InlineData("If you would like, I can pull a chart.")]
    [InlineData("Let me know if you want a scorecard next.")]
    [InlineData("Want me to run the scorecard next?")]
    [InlineData("Would you like me to break this down by store?")]
    [InlineData("Shall I convert this to a scorecard?")]
    [InlineData("Happy to run the scorecard too.")]
    [InlineData("I can also add margin outlook.")]
    [InlineData("I can next add margin outlook.")]
    public void Trailing_offer_variants_are_stripped(string offer)
    {
        string body = "Core finding: Northeast +8%.\n\n" + offer;

        string result = PlanAnswerComposer.StripTrailingOffer(body);

        result.Should().Be("Core finding: Northeast +8%.",
            $"offer variant '{offer}' should be recognized as a trailing follow-up pitch.");
    }

    [Fact]
    public void Sentence_that_only_mentions_the_word_want_mid_body_is_preserved()
    {
        // Guardrail: the opener regex must anchor to the start of the last
        // paragraph. A body sentence that merely contains "want" is not an
        // offer and must not be stripped.
        const string body =
            "Buyers want higher-margin SKUs.\n" +
            "\n" +
            "Northeast leads on this axis.";

        string result = PlanAnswerComposer.StripTrailingOffer(body);

        result.Should().Be(body);
    }

    [Fact]
    public void Only_trailing_offer_is_removed_body_and_earlier_paragraphs_survive()
    {
        const string body =
            "Paragraph one.\n" +
            "\n" +
            "Paragraph two with important detail.\n" +
            "\n" +
            "If you want, I can go deeper.";

        string result = PlanAnswerComposer.StripTrailingOffer(body);

        result.Should().Be("Paragraph one.\n\nParagraph two with important detail.");
    }

    [Fact]
    public void Reply_without_trailing_offer_is_returned_unchanged()
    {
        const string body =
            "Just the facts here.\n" +
            "\n" +
            "No conversational tail.";

        string result = PlanAnswerComposer.StripTrailingOffer(body);

        result.Should().Be(body);
    }

    // ── Whitespace / empty handling ─────────────────────────────────────

    [Fact]
    public void Whitespace_only_steps_are_skipped_and_do_not_produce_headings()
    {
        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "no-op", "   "),
            Step("scorecard", "real work", "Real answer."),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().Be("Real answer.",
            "one real step with a whitespace-only sibling must collapse to the single-step natural shape.");
        composed.Should().NotContain("## Step");
    }

    [Fact]
    public void All_empty_steps_yield_empty_string()
    {
        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "nothing", ""),
            Step("scorecard", "also nothing", "   "),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().BeEmpty();
    }

    [Fact]
    public void Reply_that_is_only_a_follow_up_offer_composes_to_empty_and_falls_through()
    {
        // Edge case: if a specialist somehow returned ONLY a follow-up offer,
        // stripping it collapses that step to empty. The single remaining
        // non-empty step must then take the natural single-step shape.
        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("scorecard", "hollow", "If you want, I can start."),
            Step("forecast", "real", "Northeast +8%."),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().Be("Northeast +8%.");
    }

    // ── Resume-path overload: merges completed + fresh steps in order ───

    [Fact]
    public void Resume_overload_orders_pre_suspend_steps_before_fresh_outcome_steps()
    {
        var completed = new List<PlanReviewCompletedStep>
        {
            new()
            {
                StepIndex = 0,
                SpecialistKey = "forecast",
                Intent = "pre-suspend work",
                Action = "act-a",
                Result = "Historical: NE +8%.",
            },
        };

        var outcome = new PlanExecutionOutcome(
            PlanId: "p1",
            Status: PlanStatus.Completed,
            FailureReason: null,
            Steps:
            [
                new PlanStepResult(
                    StepIndex: 1,
                    StepId: "p1-s1",
                    SpecialistKey: "scorecard",
                    Intent: "post-resume work",
                    Action: "act-b",
                    Status: "Completed",
                    Result: "Forward: MW +3%.",
                    Error: null,
                    InputTokens: 0,
                    OutputTokens: 0,
                    TotalTokens: 0,
                    DurationMs: 0),
            ],
            DurationMs: 0);

        string composed = PlanAnswerComposer.Compose(completed, outcome);

        int idxCompleted = composed.IndexOf("Historical: NE +8%.", StringComparison.Ordinal);
        int idxFresh = composed.IndexOf("Forward: MW +3%.", StringComparison.Ordinal);
        idxCompleted.Should().BeGreaterThanOrEqualTo(0);
        idxFresh.Should().BeGreaterThan(idxCompleted,
            "pre-suspend steps must come first so specialist order matches the chart flattening in " +
            "PlanReviewCompletionService.");
        composed.Should().Contain("## Step 1 — forecast: pre-suspend work");
        composed.Should().Contain("## Step 2 — scorecard: post-resume work");
    }

    [Fact]
    public void Resume_overload_with_only_one_non_empty_step_returns_natural_unattributed_reply()
    {
        var outcome = new PlanExecutionOutcome(
            PlanId: "p1",
            Status: PlanStatus.Completed,
            FailureReason: null,
            Steps:
            [
                new PlanStepResult(
                    StepIndex: 0,
                    StepId: "p1-s0",
                    SpecialistKey: "forecast",
                    Intent: "solo",
                    Action: "act",
                    Status: "Completed",
                    Result: "Just this one answer.",
                    Error: null,
                    InputTokens: 0,
                    OutputTokens: 0,
                    TotalTokens: 0,
                    DurationMs: 0),
            ],
            DurationMs: 0);

        string composed = PlanAnswerComposer.Compose(resumeCompletedSteps: null, outcome);

        composed.Should().Be("Just this one answer.");
    }

    // ── Heading fallback when specialist metadata is missing ────────────

    [Fact]
    public void Heading_falls_back_to_specialist_only_when_intent_is_blank()
    {
        var steps = new List<PlanAnswerComposer.StepContribution>
        {
            Step("forecast", "", "A."),
            Step("scorecard", "  ", "B."),
        };

        string composed = PlanAnswerComposer.Compose(steps);

        composed.Should().Contain("## Step 1 — forecast");
        composed.Should().Contain("## Step 2 — scorecard");
        composed.Should().NotContain("forecast:");
        composed.Should().NotContain("scorecard:");
    }
}
