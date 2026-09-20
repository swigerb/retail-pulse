using System.Text;
using System.Text.RegularExpressions;
using RetailPulse.Api.Agents.Planning;
using RetailPulse.Contracts.Approval;

namespace RetailPulse.Api.Approval;

/// <summary>
/// Composes the final user-visible reply for a plan-first turn from the
/// per-specialist step results, replacing the naive <c>---</c> concatenation
/// that produced the "two stapled answers" bug in #301.
///
/// <para>
/// Rules (single source of truth for both the fresh and the resume path):
/// </para>
/// <list type="bullet">
///   <item>Skip steps with empty/whitespace results.</item>
///   <item>Strip trailing conversational follow-up offers ("If you want, I
///     can…", "Let me know if…", "Want me to…", etc.) from each step's
///     result so the composed answer does not read like two half-answers
///     each pitching more work.</item>
///   <item>A single non-empty step is returned verbatim (after offer strip)
///     with <b>no</b> attribution header — a single-specialist answer must
///     read as one natural response.</item>
///   <item>Two or more non-empty steps are joined with attribution headings
///     that name the specialist and the step's intent, separated by blank
///     lines (no <c>---</c> rule).</item>
/// </list>
/// </summary>
internal static partial class PlanAnswerComposer
{
    // Bullet/enumerated continuations of an offer paragraph (e.g. numbered
    // "1. historical demand 2. 90-day forecast …") count as part of the
    // trailing offer and are stripped with it. Kept permissive: any block
    // whose first non-blank line matches an offer opener is stripped, along
    // with every subsequent line up to end-of-string.
    [GeneratedRegex(
        @"^\s*(?:" +
            @"If\s+you(?:'d|\s+would)?\s+(?:want|like)\b" +
            @"|Let\s+me\s+know\s+if\b" +
            @"|Want\s+me\s+to\b" +
            @"|Would\s+you\s+like\s+me\s+to\b" +
            @"|Shall\s+I\b" +
            @"|Happy\s+to\b" +
            @"|I\s+can\s+(?:also|next|now)\b" +
        @")",
        RegexOptions.IgnoreCase)]
    private static partial Regex OfferOpenerRegex();

    /// <summary>
    /// Normalized view of a step for composition. Charts / tokens are irrelevant
    /// to the reply text and stay on their original records.
    /// </summary>
    internal readonly record struct StepContribution(
        string SpecialistKey,
        string Intent,
        string Result);

    /// <summary>
    /// Compose the final reply. Returns <see cref="string.Empty"/> when there
    /// is nothing to say — callers keep their existing "orchestrator produced
    /// no output" fallback for that case.
    /// </summary>
    public static string Compose(IReadOnlyList<StepContribution> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var kept = new List<StepContribution>(steps.Count);
        foreach (StepContribution s in steps)
        {
            if (string.IsNullOrWhiteSpace(s.Result)) continue;
            string trimmed = StripTrailingOffer(s.Result).TrimEnd();
            if (trimmed.Length == 0) continue;
            kept.Add(s with { Result = trimmed });
        }

        if (kept.Count == 0) return string.Empty;

        // Single-step: no attribution header — a solo specialist reply must
        // read as one natural response, not a report of one.
        if (kept.Count == 1) return kept[0].Result;

        var sb = new StringBuilder();
        for (int i = 0; i < kept.Count; i++)
        {
            if (i > 0) sb.AppendLine().AppendLine();
            sb.Append(BuildHeading(i + 1, kept[i]));
            sb.AppendLine().AppendLine();
            sb.Append(kept[i].Result);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compose from the two per-step shapes the review resume path carries —
    /// pre-suspend <see cref="PlanReviewCompletedStep"/> records and post-resume
    /// <see cref="PlanStepResult"/> records. Pre-suspend steps come first so
    /// specialist order matches
    /// <see cref="PlanReviewCompletionService"/>'s chart flattening.
    /// </summary>
    public static string Compose(
        IReadOnlyList<PlanReviewCompletedStep>? resumeCompletedSteps,
        PlanExecutionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var normalized = new List<StepContribution>();
        if (resumeCompletedSteps is not null)
        {
            foreach (PlanReviewCompletedStep s in resumeCompletedSteps)
            {
                normalized.Add(new StepContribution(s.SpecialistKey, s.Intent, s.Result));
            }
        }
        foreach (PlanStepResult s in outcome.Steps)
        {
            normalized.Add(new StepContribution(s.SpecialistKey, s.Intent, s.Result));
        }
        return Compose(normalized);
    }

    private static string BuildHeading(int stepNumber, StepContribution step)
    {
        string specialist = string.IsNullOrWhiteSpace(step.SpecialistKey)
            ? "specialist"
            : step.SpecialistKey.Trim();
        string intent = (step.Intent ?? string.Empty).Trim();
        return intent.Length == 0
            ? $"## Step {stepNumber} — {specialist}"
            : $"## Step {stepNumber} — {specialist}: {intent}";
    }

    /// <summary>
    /// Strip the trailing conversational follow-up offer, if any. The offer
    /// is the last paragraph whose first non-blank line matches one of the
    /// canonical opener phrases — everything from that opener to the end of
    /// the string is dropped. Body content above the last blank line is
    /// preserved verbatim.
    /// </summary>
    internal static string StripTrailingOffer(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Normalize line endings for scanning; recompose using the original
        // separator on emit so we don't rewrite CRLF ↔ LF for downstream.
        string[] lines = text.Split('\n');

        // Find start of the last paragraph (paragraph = block separated by
        // one or more blank lines). Walk backward: skip trailing blanks,
        // then walk up while non-blank; the index after the last blank is
        // the first line of the last paragraph.
        int end = lines.Length - 1;
        while (end >= 0 && IsBlank(lines[end])) end--;
        if (end < 0) return text;

        int start = end;
        while (start > 0 && !IsBlank(lines[start - 1])) start--;

        // Only strip when the last paragraph *starts* with an offer opener —
        // this avoids nuking a real body paragraph that merely mentions the
        // word "want" mid-sentence.
        string firstLine = lines[start].TrimStart('>', '*', '-', ' ', '\t');
        if (!OfferOpenerRegex().IsMatch(firstLine)) return text;

        // Drop the last paragraph AND the blank lines immediately preceding
        // it, so the caller's TrimEnd() gets a clean tail.
        int cut = start;
        while (cut > 0 && IsBlank(lines[cut - 1])) cut--;

        return cut == 0 ? string.Empty : string.Join('\n', lines, 0, cut);
    }

    private static bool IsBlank(string line) =>
        string.IsNullOrWhiteSpace(line);
}
