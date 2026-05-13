namespace RetailPulse.Api.Rag;

/// <summary>
/// Seeds the knowledge base with sample retail/CPG documents on startup.
/// Idempotent — skips documents that are already ingested.
/// </summary>
public static class KnowledgeBaseSeeder
{
    public static async Task SeedAsync(InMemoryKnowledgeBase kb, ILogger logger, CancellationToken ct = default)
    {
        var sampleDocs = GetSampleDocuments();
        var ingested = 0;

        foreach (var (title, source, content) in sampleDocs)
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

    private static List<(string Title, string Source, string Content)> GetSampleDocuments() =>
    [
        ("Holiday Planning Guide", "holiday-planning-guide.md", HolidayPlanningGuide),
        ("Category Management Playbook", "category-management-playbook.md", CategoryManagementPlaybook),
        ("Promotion Effectiveness Report", "promotion-effectiveness-report.md", PromotionEffectivenessReport),
        ("Competitive Response Framework", "competitive-response-framework.md", CompetitiveResponseFramework)
    ];

    private const string HolidayPlanningGuide = """
        # Holiday Planning Guide: Seasonal Best Practices for Retail & CPG

        ## Planning Timeline

        Successful holiday execution begins 16-20 weeks before the season. Major retailers start holiday planning in June for Q4 execution. The critical milestones include: initial assortment planning (Week -20), promotional calendar lock (Week -16), inventory position review (Week -12), display and POS material production (Week -8), and store-level execution (Week -4).

        Early planning correlates strongly with holiday performance. Retailers who finalize assortments by August see 12-18% higher sell-through rates compared to those who delay until September. This is because early commitment enables better supplier negotiations, more lead time for exclusive products, and longer windows for team training.

        ## Display Strategy

        Holiday displays should go up in early October for maximum impact. Research shows that early-season displays (before October 15) generate 23% more incremental revenue than late-season setups. End-cap displays outperform inline placements by 3.2x during the holiday season, compared to 2.1x during regular periods.

        Themed displays that tell a story — such as "Holiday Entertaining" or "Gift Sets Under $25" — outperform generic seasonal displays by 40%. Cross-category bundling on displays (e.g., spirits + mixers + snacks) increases basket size by an average of $12.50 per transaction.

        ## Inventory Management

        Holiday inventory should follow a "build-peak-clear" model. Build inventory gradually from Week -8 to Week -2, peak inventory levels at Week 0 (Black Friday week), and aggressively promote clearance starting December 26. Safety stock should increase by 25-35% above normal levels for top-selling items.

        The most common holiday inventory mistake is over-indexing on last year's winners. Consumer preferences shift annually — allocate 15-20% of holiday shelf space to new items and trending products. Use demand forecasting tools to model scenarios and adjust weekly.

        ## Staffing and Training

        Holiday staffing should increase by 30-50% in high-traffic locations. However, the quality of seasonal hires matters more than quantity. Retailers who invest in 4+ hours of product knowledge training for seasonal staff see 15% higher conversion rates and 8% higher average transaction values.

        Schedule training sessions in early October, focusing on: key promotional items, upselling techniques, gift recommendation skills, and return/exchange policies. Role-playing exercises for handling difficult customer situations reduce holiday escalations by 22%.
        """;

    private const string CategoryManagementPlaybook = """
        # Category Management Playbook: Fundamentals for Retail Excellence

        ## Category Definition and Role Assignment

        Effective category management begins with clear category definitions and role assignments. Every category should be classified into one of four roles: Destination (drives store trips, 5-7% of categories), Routine (fills regular needs, 55-65%), Seasonal (time-bound demand, 15-20%), and Convenience (impulse and fill-in, 10-20%).

        Category roles determine space allocation, promotional intensity, and pricing strategy. Destination categories receive premium placement and aggressive pricing to drive traffic. Routine categories optimize for margin while maintaining competitive price perception. Seasonal categories require dynamic space allocation that shifts throughout the year.

        ## Assortment Optimization

        The 80/20 rule applies strongly to retail assortments: typically 20% of SKUs generate 80% of category revenue. However, the long tail serves important purposes — variety drives perception of selection, which influences store choice for 43% of shoppers. The goal is not to eliminate the long tail but to optimize its footprint.

        SKU rationalization should be data-driven: analyze velocity (units/store/week), margin contribution, substitutability, and customer loyalty. Items below 0.5 units/store/week with available substitutes are candidates for delisting. Before cutting, verify the item doesn't serve a key demographic — removing the only organic or gluten-free option in a category can cost more than the shelf space saved.

        ## Pricing Architecture

        Category pricing should follow a Good-Better-Best framework. The "Good" tier anchors value perception and should be within 5% of key competitors. The "Better" tier is the profit engine — priced 20-40% above Good with meaningful quality differentiation. The "Best" tier drives aspiration and establishes category authority, priced 60-100%+ above Good.

        Price gaps between tiers should be consistent and logical. If the gap between Good and Better is $3, the gap between Better and Best should be $4-6. Inconsistent gaps create confusion and can push consumers to the wrong tier for their needs.

        ## Shelf Space Allocation

        Space-to-sales ratios should be reviewed quarterly. Categories with space-to-sales ratios above 1.2 are over-spaced; below 0.8 are under-spaced. Adjust by 10-15% per quarter to avoid disrupting established shopping patterns. Use planogram compliance data to ensure field execution matches headquarters intent.

        Vertical blocking by brand outperforms horizontal layouts for most categories. Shoppers scan shelves vertically first, so stacking brand variants vertically improves findability by 18% and reduces out-of-stock perception by 12%.
        """;

    private const string PromotionEffectivenessReport = """
        # Promotion Effectiveness Report: Historical Analysis and Insights

        ## Executive Summary

        Analysis of 850+ promotional campaigns across 12 brands and 6 regions over the past 18 months reveals significant variance in promotional effectiveness. The average promotional lift was 24.3%, but this masks a wide distribution: top-quartile promotions delivered 40%+ lift while bottom-quartile delivered less than 10%.

        The single strongest predictor of promotional success is alignment between promo type and category dynamics. Discount promotions work best for price-sensitive categories (grocery staples, household goods), while BOGO and bundle promotions outperform in categories where consumers stockpile (beverages, snacks, personal care).

        ## Promotion Type Analysis

        Discount promotions (30% of campaigns): Average lift of 22%, but highest variance. Deep discounts (>25% off) deliver diminishing returns — the lift from 30% off is only 15% higher than from 20% off, but margin erosion doubles. The sweet spot is 15-20% off for 2-3 weeks.

        BOGO promotions (20% of campaigns): Average lift of 31%, the highest of any type. However, BOGO promotions carry the highest risk of pantry loading — 40% of incremental volume represents pulled-forward demand rather than true growth. Optimal for trial-generating purposes in categories with low repeat purchase rates.

        Display promotions (20% of campaigns): Average lift of 19%, but with the longest tail effect. Display-only promotions (no price reduction) maintain elevated sales for 2-3 weeks after the display is removed, compared to 0-1 weeks for price-based promotions. Best for new products and seasonal items.

        Digital promotions (20% of campaigns): Average lift of 17%, but with the lowest cost-per-incremental-unit. Digital coupons and targeted offers cost 40-60% less per incremental unit than traditional FSI or in-store promotions. ROI is highest when combined with in-store display support.

        Bundle promotions (10% of campaigns): Average lift of 26% on the anchor product, with 35% attach rate on companion products. Most effective when the bundle crosses categories (e.g., spirits + mixers, cereal + milk) and saves the consumer 10-15% vs. buying items separately.

        ## Regional Patterns

        The Southeast region consistently outperforms on promotional lift (+15% vs. national average), likely due to higher price sensitivity and lower baseline promotional intensity. The West Coast shows the highest digital promotion adoption (2.3x national average) but lowest response to traditional print promotions.

        ## Timing Insights

        Promotions launched on Monday-Wednesday outperform Thursday-Sunday launches by 8%. This is counter-intuitive but explained by the "discovery effect" — mid-week shoppers have more time to notice and respond to new promotions, creating word-of-mouth that amplifies weekend traffic.

        Avoid running promotions during the first two weeks of January and the last two weeks of August — consumer spending contraction during these periods reduces promotional responsiveness by 20-30%.
        """;

    private const string CompetitiveResponseFramework = """
        # Competitive Response Framework: Responding to Competitor Moves

        ## Response Triage

        Not every competitive move requires a response. Use the Impact-Urgency Matrix to prioritize:
        - High Impact + High Urgency (respond within 48 hours): Competitor price cut on your top-10 items, new store opening within 3 miles, aggressive promotional blitz in your stronghold market.
        - High Impact + Low Urgency (develop response in 2-4 weeks): Competitor launches private label in key category, new competitor enters market, competitor expands delivery coverage.
        - Low Impact + High Urgency (monitor, minimal response): Competitor one-time event pricing, social media campaign targeting your brand, competitor hiring your staff.
        - Low Impact + Low Urgency (log and review quarterly): Minor assortment changes, corporate announcements, executive changes.

        ## Price Response Strategies

        When a competitor cuts price on a key item, avoid the reflex to match immediately. Instead, follow the 3-step price response protocol:

        Step 1: Verify the competitor's price is sustainable. Is this a temporary promotion (check flyer/digital ads) or a permanent price reduction? If temporary, consider whether your regular price still falls within an acceptable competitive index (target: 95-105% of competitor).

        Step 2: Assess cross-elasticity. Will the competitor's price cut actually pull your customers? In many categories, brand loyalty and convenience outweigh price differences of 5-10%. Use loyalty card data to identify price-sensitive segments vs. convenience-driven segments.

        Step 3: Choose response type. Options include: match price on the specific item (lowest cost, highest margin hit), respond with a bundle offer (protects margin, adds value), increase promotional frequency (temporary response), or invest in service/experience differentiation (long-term moat).

        ## New Competitor Entry

        When a new competitor enters your market, the first 90 days are critical. Research shows that market share lost in the first 90 days is 3x harder to recover than share lost after the initial period.

        Defensive tactics for new competitor entry: increase promotional intensity by 15-20% in affected trade area (not chain-wide), enhance loyalty program offers for at-risk customers, deploy "store visit" offers (e.g., $5 off $50 purchase) to maintain traffic patterns, fast-track any planned store improvements or remodels.

        Avoid the "scorched earth" response of massive price cuts across all categories — this signals weakness and is rarely sustainable. Instead, target your response to the categories and customer segments where the new competitor is strongest.

        ## Market Intelligence

        Effective competitive response requires continuous intelligence. Assign competitive monitoring responsibilities: store managers should visit competitor stores bi-weekly and report pricing, assortment, and display changes. Category managers should track competitor digital presence (website, app, social media) weekly.

        Build a competitive intelligence dashboard that tracks: competitor promotional frequency, price index on key items (weekly), customer survey data on competitor perception (quarterly), and market share data (monthly). Share competitive intelligence broadly — field teams often have the best early warning of competitive moves.

        ## Long-term Competitive Advantage

        The most effective competitive strategy is not reactive — it's building advantages that competitors cannot easily replicate. Focus on: exclusive product agreements with key suppliers, proprietary data and analytics capabilities, superior employee training and retention, community engagement and local relevance, and seamless omnichannel experience. These structural advantages create compounding returns and make reactive price wars less frequent and less necessary.
        """;
}
