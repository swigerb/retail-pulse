namespace RetailPulse.Tests.Rag.Baselines;

/// <summary>
/// Fixed, versioned corpus + query set for the pre-Wave-5 InMemory BM25
/// regression baseline. The values here are the deterministic inputs the
/// golden fixture <c>inmemory-pre-wave5.json</c> was generated from.
///
/// The corpus intentionally uses retail-adjacent, non-secret vocabulary so
/// baseline diffs are readable in review. Do NOT edit these values — a diff
/// here IS a diff of the baseline artifact and must be treated as a
/// deliberate behavioural change under the Wave 5 optionality guarantee.
/// </summary>
internal static class PreWave5BaselineFixture
{
    public const string FixtureVersion = "1.0.0-pre-wave5";

    public static readonly IReadOnlyList<(string Title, string Source, string Content)> Corpus =
    [
        (
            "Category Management Playbook",
            "playbook.md",
            "Category management defines the role, strategy, and metrics for every merchandising category. " +
            "Category captains coordinate with suppliers on planograms, promotional cadence, and assortment. " +
            "The end goal is category-level growth measured against a defined benchmark."),
        (
            "Holiday Planning Guide",
            "guide.md",
            "Holiday displays should be set in early October to maximize impact. " +
            "Themed holiday displays outperform generic seasonal displays year over year. " +
            "Ensure holiday-specific SKUs are protected from stockouts through the peak weeks."),
        (
            "Supplier Terms Standards",
            "standards.md",
            "Supplier terms must include on-time delivery penalties, quality inspection windows, and returns policy. " +
            "Terms are renegotiated annually and reviewed against category benchmarks."),
        (
            "Planogram Compliance Standard",
            "compliance.md",
            "Planogram compliance is measured weekly by store-level audit teams. " +
            "Non-compliant fixtures must be corrected within 48 hours. " +
            "Repeat non-compliance triggers a category review."),
        (
            "Assortment Optimization Notes",
            "notes.md",
            "Assortment optimization balances SKU productivity, category coverage, and shelf space efficiency. " +
            "Slow movers below a defined velocity threshold are candidates for deletion each quarter."),
    ];

    public static readonly IReadOnlyList<string> Queries =
    [
        "category management",
        "holiday displays",
        "supplier delivery penalties",
        "planogram compliance audit",
        "assortment velocity",
        "shelf space efficiency slow movers",
    ];
}
