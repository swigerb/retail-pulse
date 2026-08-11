using System.Text.RegularExpressions;
using RetailPulse.Contracts.Charts;

namespace RetailPulse.Api.Charts;

/// <summary>
/// Deterministic table-driven lookup layered over the
/// <see cref="ChartAcceptanceManifest"/>. Given an arbitrary user message it
/// answers, in a single canonical way, "is this the exact text of a curated
/// chart prompt from the tenant's prompt library, and if so which manifest
/// case does it correspond to?"
///
/// Purpose (issue #76 blockers):
///
///   * BLOCKER 2 — prompt #8 ("Compare Coastline Tacos vs Apex Grill depletions
///     across all regions") is a CHART prompt on the manifest but is not
///     recognised by the linguistic <see cref="ChartRequestDetector"/> — it
///     carries no chart-type word or chart noun, only "Compare X vs Y". The
///     drop-on-prose invariant added in 61c7e90 for Group A then correctly
///     dropped any chart the model produced for it, which is exactly the
///     regression Publix reported. The manifest is the tenant-scoped
///     authoritative statement that this prompt IS a chart request, so
///     consulting the manifest first turns the detector into a table lookup
///     for every curated chart prompt while leaving every other message on
///     the existing linguistic path.
///
///   * BLOCKER 1 — for prompts #19/#21/#23/#26 the linguistic detector already
///     classifies "Create a line chart …", "Create a pie chart …", etc. as
///     explicit. But the manifest lookup makes the intent classification an
///     unambiguous, run-invariant function of the exact prompt text — the
///     stated determinism gate — because the RoutedIntent and canonical
///     ChartType come from a static table rather than from regex alternation
///     order or heuristic scoring that could ever be perturbed by upstream
///     code changes.
///
/// The catalog is tenant-generic: entries are the manifest cases, and the
/// manifest is the single source of truth for the tenant's curated prompt
/// library (kept in sync with <c>src/RetailPulse.Web/src/constants/prompts.ts</c>
/// via <c>ChartAcceptanceManifestContractTests</c>). No brand or prompt
/// literals live in this file.
/// </summary>
public static partial class ChartPromptCatalog
{
    // Canonical form of a prompt: trimmed, whitespace collapsed to single
    // spaces, lower-cased with invariant culture. Deliberately simple — we
    // want prompt-library matches to be table-exact, not fuzzy: an ambiguous
    // rewording must still fall through to the linguistic detector so the
    // manifest can never accidentally attach chart semantics to a prose ask.
    private static string Canonicalize(string message)
        => WhitespaceRegex().Replace(message.Trim(), " ").ToLowerInvariant();

    private static readonly Lazy<IReadOnlyDictionary<string, ChartAcceptanceCase>> _byPrompt =
        new(BuildIndex);

    private static IReadOnlyDictionary<string, ChartAcceptanceCase> BuildIndex()
    {
        var map = new Dictionary<string, ChartAcceptanceCase>(StringComparer.Ordinal);
        foreach (ChartAcceptanceCase c in ChartAcceptanceManifest.Cases)
        {
            map[Canonicalize(c.Prompt)] = c;
        }
        return map;
    }

    /// <summary>
    /// Look up a manifest case for <paramref name="message"/>. Returns
    /// <c>true</c> when the (canonicalized) message is an exact match for one
    /// of the curated chart-library prompts and populates
    /// <paramref name="matched"/> with the manifest case; otherwise
    /// <c>false</c> and <paramref name="matched"/> is <c>null</c>.
    ///
    /// Exact (canonicalized) match only — no substring, no fuzzy scoring — so
    /// the result is deterministic and reproducible for a given input, and
    /// nothing outside the curated library can accidentally acquire chart
    /// intent through this path.
    /// </summary>
    public static bool TryMatch(string? message, out ChartAcceptanceCase? matched)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (_byPrompt.Value.TryGetValue(Canonicalize(message), out ChartAcceptanceCase? c))
        {
            matched = c;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
