using RetailPulse.Api.Packs;

namespace RetailPulse.Api.Rag;

/// <summary>
/// Seeds the in-memory knowledge base with the sample tenant's grounding
/// corpus on startup. Idempotent — skips documents already present.
///
/// The four sample documents are fictional and explicitly attributed to
/// "Apex Retail Group." They cover the four grounding surfaces exercised by
/// per-agent knowledge binding (issue #105):
/// planogram/shelf-set, supplier/distributor service, merchandising/promo
/// execution, and category/assortment. Filenames match the values used in
/// <c>appsettings.json</c> under <c>Knowledge:Sources:Named</c> so the
/// startup source-name validation in
/// <see cref="KnowledgeSourceRegistry"/> aligns with the seeded corpus.
/// </summary>
public static class KnowledgeBaseSeeder
{
    public static async Task SeedAsync(InMemoryKnowledgeBase kb, ILogger logger, CancellationToken ct = default)
    {
        IReadOnlyList<(string Title, string Source, string Content)> sampleDocs = GetSampleDocuments();
        int ingested = 0;

        foreach ((string? title, string? source, string? content) in sampleDocs)
        {
            if (kb.HasDocument(title))
            {
                logger.LogDebug("Skipping already-ingested document: {Title}", title);
                continue;
            }

            await kb.IngestDocumentAsync(title, content, source, ct);
            ingested++;
        }

        logger.LogInformation("Knowledge base seeding complete: {Ingested} new documents, {Total} total ({Chunks} chunks)",
            ingested, kb.DocumentCount, kb.ChunkCount);
    }

    /// <summary>
    /// Ingest a loaded content pack's grounding corpus into
    /// <paramref name="kb"/>. Content-hash aware: an unchanged pack does
    /// NOT reseed, but a real content change (same source, different
    /// body) purges the stale document and re-ingests the new one so
    /// operators never see stale grounding after a pack update.
    /// </summary>
    /// <remarks>
    /// Idempotent by the pair (source, content-hash). Two invocations
    /// with the same pack are a no-op; two invocations with different
    /// packs share the store safely — a document whose source is not
    /// present in the new pack stays until the caller purges it (fresh
    /// process starts always begin with an empty in-memory store).
    /// </remarks>
    public static async Task SeedAsync(
        InMemoryKnowledgeBase kb,
        LoadedPack pack,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(kb);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(logger);

        int ingested = 0;
        int refreshed = 0;
        int skipped = 0;

        foreach (PackKnowledgeDocument doc in pack.KnowledgeDocuments)
        {
            string contentHash = PackContentFingerprint.ComputeContentHash(doc.Content);

            if (kb.HasDocumentWithContent(doc.Source, contentHash))
            {
                logger.LogDebug(
                    "Pack '{Pack}': skipping unchanged knowledge doc {Source} (hash {Hash})",
                    pack.Name, doc.Source, contentHash[..8]);
                skipped++;
                continue;
            }

            int removed = kb.RemoveDocumentsBySource(doc.Source);
            await kb.IngestDocumentAsync(doc.Title, doc.Content, doc.Source, ct).ConfigureAwait(false);

            if (removed > 0)
            {
                refreshed++;
            }
            else
            {
                ingested++;
            }
        }

        logger.LogInformation(
            "Pack '{Pack}' knowledge seeding complete: {Ingested} new, {Refreshed} refreshed, {Skipped} unchanged. " +
            "Total docs in store: {Total} ({Chunks} chunks).",
            pack.Name, ingested, refreshed, skipped, kb.DocumentCount, kb.ChunkCount);
    }

    /// <summary>
    /// Public list of the default-pack sample documents. Exposed so the
    /// pack-loader equivalence tests can pin the shipped
    /// <c>packs/default/knowledge/*.md</c> corpus to the seeder's own
    /// content byte-for-byte, catching drift the moment either side
    /// changes.
    /// </summary>
    public static IReadOnlyList<(string Title, string Source, string Content)> GetSampleDocuments() =>
    [
        ("Apex Planogram & Shelf-Set Reference", "apex-planogram-shelf-set.md", _planogramShelfSet),
        ("Apex Supplier & Distributor Service Levels", "apex-supplier-service-levels.md", _supplierServiceLevels),
        ("Apex Merchandising & Promo Execution", "apex-merchandising-promo-execution.md", _merchandisingPromoExecution),
        ("Apex Category & Assortment Reference", "apex-category-assortment.md", _categoryAssortment),
    ];

    private const string _planogramShelfSet = """
        # Apex Retail Group — Planogram & Shelf-Set Reference (Sample)

        > Sample corpus notice. This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." It contains no real customer data, no real supplier data, and no proprietary retailer material.

        ## Planogram Cadence

        Apex banners refresh planograms on a rolling six-week cadence tied to the internal Shelf-Set Wave calendar. Every wave carries a Wave ID (for example Wave-2026-14) and a scoped set of categories so field teams execute one banner-category slice at a time. Grocery banners (FreshMart, Harvest Table) refresh in odd waves; Spirits banners (Sierra Gold, Ridgeline, Summit Vodka) refresh in even waves; Quick-Serve, Home Improvement, Office Supply, and Furniture banners each roll on their own two-wave cycle.

        A shelf-set is considered live only after the planogram compliance photo has been uploaded and scored above the acceptance threshold for the banner. Threshold defaults live in the tenant configuration.

        ## Shelf-Set Compliance Fields

        Every shelf-set reset records banner_id, store_id, wave_id, set_date, compliance_score (0-100 acceptance score from the compliance photo), facings_planned vs. facings_actual (product facings from the planogram vs. counted in-store), and void_count (empty facings observed during audit).

        A store is flagged for coaching when three consecutive resets land below the banner acceptance floor. Coaching flags never auto-close — the banner merchandising lead has to sign off.

        ## Category Slotting Guardrails

        Apex applies two hard guardrails when slotting a category. Anchor Placement: Category anchors (top three velocity SKUs, banner-neutral) must occupy the two most valuable eye-level bays. The category manager cannot slot a new SKU into an anchor position without documented replacement approval. Adjacent-Set Continuity: Two related planograms sharing a bay (for example spirits mixers next to Ridgeline Bourbon anchors) must publish resets in the same Wave. Cross-wave adjacency creates orphan facings and drops compliance scores.

        ## Reset Escalations

        When a store misses a scheduled reset date, the workflow escalates in three steps. A store-level task opens with a 48-hour window. If the task ages out, the district merchandising manager is notified. If the reset is still open the following Monday, a banner-level exception review runs.

        Escalations are logged as planogram.reset_escalation events. Downstream analytics uses those events to rebuild banner-level reset health without needing raw store operations data.

        ## Shelf-Set Vocabulary

        Wave — the scheduled reset cycle window. Anchor — the top velocity SKUs a category keeps at eye level. Void — a facing planned by the planogram but empty at audit time. Continuity Pair — two planograms that share a bay and reset together. Coaching Flag — the marker raised when a store misses reset acceptance three cycles in a row.
        """;

    private const string _supplierServiceLevels = """
        # Apex Retail Group — Supplier & Distributor Service Levels (Sample)

        > Sample corpus notice. This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." Distributor and supplier names, tiers, and metrics are illustrative only.

        ## Three-Tier Distribution Model

        Apex operates a Three-Tier distribution model (Distributor to Wholesaler to Retailer). Every Apex banner is served by a portfolio of Tier-1 primary distributors and Tier-2 backup distributors. Distributor assignment is banner-scoped: FreshMart's Tier-1 for chilled goods is not necessarily Sierra Gold Tequila's Tier-1 for spirits.

        ## Service Level Metrics

        Apex tracks four supplier and distributor service metrics against the sample tenant service-level agreement. Fill Rate is cases shipped divided by cases ordered on the same purchase order, with a sample-tenant SLA floor of 96 percent. On-Time Delivery is the share of deliveries arriving inside the delivery window on the purchase order, with a floor of 94 percent. Order Accuracy is line items delivered with no substitutions and no case-count variance, floor 97 percent. Damage Rate is cases arriving damaged divided by cases shipped, floor below 1.0 percent.

        A distributor falls into an SLA breach state when any two of these metrics stay below floor for two consecutive review periods. The distributor's next replenishment run is downgraded from Tier-1 to Tier-2 until the review board clears the breach.

        ## Fill-Rate Investigation Workflow

        When fill rate drops on a specific banner or category slice, the supply chain analyst pulls order coverage (did the purchase order request quantities the distributor could realistically supply for that region and week), distributor allocation (was Apex's slice of the distributor's regional capacity honored), the substitution log (were substitutes accepted at the receiving door or rejected as short-shipped), and backhaul exceptions (were any deliveries pushed to the next window due to backhaul consolidation).

        The analyst records findings against the affected purchase order lines. Findings feed the monthly distributor scorecard.

        ## Backup Distributor Activation

        Backup distributors get activated when the primary distributor's fill rate stays below floor for the banner for three consecutive weeks or when a single event drops fill rate below 80 percent for one week. Activation is a documented, banner-scoped decision — it never fires globally across all Apex banners at once.

        ## Distributor Scorecard Grades

        Distributor scorecards use a five-band grade. Grade A: all four service metrics above floor for the review period. Grade B: one metric below floor, none in breach state. Grade C: two metrics below floor, breach clock started. Grade D: breach confirmed, backup distributor activated for affected lines. Grade F: sustained breach across two review periods, contract renegotiation triggered per the sample tenant governance policy.

        ## Vocabulary

        Fill Rate — the primary service-level metric distributors negotiate against. Backhaul — return trip freight consolidation that can defer delivery windows. Tier-1 or Tier-2 — primary vs. backup distributor status for a banner. SLA Breach — the escalated state a distributor enters after two review periods below floor on two or more metrics.
        """;

    private const string _merchandisingPromoExecution = """
        # Apex Retail Group — Merchandising & Promo Execution (Sample)

        > Sample corpus notice. This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." Promotion mechanics, dates, and thresholds below are illustrative only.

        ## Promo Windows

        Apex runs promotions inside named windows tied to the banner's fiscal calendar. Every promo window has a Window ID (for example PW-2026-Q1-03), a scoped list of banners, and a set of participating categories. Windows never span more than a single banner's fiscal period.

        ## Promo Types and Owners

        Feature promos are owned by the banner merchandising lead and give a highlighted placement in the weekly ad plus an optional endcap. Endcap Display promos are owned by store operations, are planogram-driven, and require shelf-set continuity. Bundle promos are owned by the category manager and sell two or more SKUs together at a promo price. Threshold Discount promos are owned by the pricing lead and apply at basket threshold (spend or unit count). Sample or Demo promos are owned by field marketing and are in-store activations with staffed sampling in Apex-approved zones. A promo record without an accountable owner does not clear the promo review gate.

        ## Store-Level Execution Checklist

        Store operations validates every promo through five checks before the window opens. POS Sync: pricing rules present in the point-of-sale before the first sale of the window. Signage: required signage kits printed and hung in the primary and secondary zones. Endcap Shelf-Set: endcap facings match the promo planogram and adjacencies are preserved. Inventory Cover: projected sell-through covers the promo window with the banner's safety buffer. Team Brief: store team confirms the daily brief line item that describes the promo.

        Missed checks generate promo.execution_exception events. Field sentiment surfaces recurring exception patterns to the pod that owns the affected banner.

        ## Endcap Adjacency Rules

        Endcap displays follow Apex adjacency rules to avoid confusing shopper flow. Two competing endcaps for the same banner do not run in the same store during overlapping windows. Cross-banner endcap adjacencies (for example a Sierra Gold Tequila endcap next to a FreshMart chilled mixers endcap) must publish coordinated planograms in the same Shelf-Set Wave. Endcaps required by Feature promos always outrank Endcap Display promos when a store has limited endcap inventory.

        ## Post-Window Review

        Every promo window closes with a two-pass review. The ops review asks whether the store-level execution checks landed inside their SLA windows. The category review asks whether the promo hit its unit and revenue targets and whether adjacent categories cannibalized or lifted. Both reviews land in the banner post-window packet.

        ## Vocabulary

        Promo Window — the named calendar slot inside which a promo runs. Endcap Kit — the signage, planogram, and inventory bundle a store receives before an endcap display goes live. Execution Exception — a promo check that failed store-level acceptance. Sell-Through Cover — the ratio of projected available units to promo demand for the window.
        """;

    private const string _categoryAssortment = """
        # Apex Retail Group — Category & Assortment Reference (Sample)

        > Sample corpus notice. This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." Category segmentation, review cadences, and thresholds below are illustrative and not benchmarks.

        ## Category Roles

        Every Apex category is assigned exactly one role at each banner. Roles are reviewed twice a fiscal year. Destination categories draw banner-defining trips and carry full depth, with exclusive lines allowed. Routine categories fill recurring baskets with moderate depth and competitive parity. Occasion categories anchor gifting or seasonal moments and their depth ramps with the seasonal window. Fill-In categories are convenience adjacencies to core baskets and stay narrow, curated, with no long-tail.

        A category cannot hold two roles at the same banner at the same time. A Ridgeline Bourbon anchor at a spirits banner can be Destination; at a grocery banner the same brand may play a Fill-In role.

        ## Assortment Review Cycle

        Assortment reviews follow a documented eight-step flow. Step one confirms the category role. Step two ingests consumer demand signals from the demand forecast pod. Step three reviews distributor and supplier service posture. Step four checks planogram feasibility with the current Shelf-Set Wave. Step five reviews margin architecture across Good, Better, and Best tier gaps. Step six covers innovation slot analysis — how many new SKUs the assortment can absorb. Step seven identifies rationalization candidates below the banner's velocity floor. Step eight runs a portfolio depletion check on affected suppliers.

        Reviews close with a decision packet stamped by the category manager and the banner merchandising lead.

        ## Assortment Guardrails

        Apex enforces four assortment guardrails during a review. Anchor Retention keeps anchors identified in the planogram reference in the assortment unless a rationalization case is documented and countersigned by the banner merchandising lead. The Regional Exception Register holds region-specific SKUs (for example West Coast citrus mixers) and re-validates them every review. Innovation Cap limits a single review to no more than two new SKUs per linear foot of shelf. Depletion Guard requires a supplier depletion review so an exit does not strand contract volume.

        ## Rationalization Signals

        SKUs enter rationalization scrutiny when any two of the following hold for two consecutive review periods: velocity below the banner's category-level floor, zero incremental basket association (no meaningful attach behavior), persistent void flags from the planogram reference, or sustained margin below the banner category floor. A single soft signal is a note, not a rationalization case. Rationalization cases are treated as reversible for two review periods so the category can be recovered if the exit hurts the assortment.

        ## Vocabulary

        Anchor — the top-velocity SKUs a category keeps in prime shelf positions. Category Role — the strategic role the category plays for a banner. Innovation Slot — the reserved capacity for new SKUs during a review. Depletion Guard — the supplier-side check performed before delisting a SKU.
        """;
}
