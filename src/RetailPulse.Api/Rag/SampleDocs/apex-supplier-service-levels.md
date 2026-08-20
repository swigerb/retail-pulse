# Apex Retail Group — Supplier & Distributor Service Levels (Sample)

> **Sample corpus notice.** This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." Distributor and supplier names, tiers, and metrics are illustrative only. Nothing here reflects real-world contract terms or third-party performance.

## 1. Three-Tier Distribution Model

Apex operates a Three-Tier distribution model (Distributor → Wholesaler → Retailer). Every Apex banner is served by a portfolio of Tier-1 primary distributors and Tier-2 backup distributors. Distributor assignment is banner-scoped: FreshMart's Tier-1 for chilled goods is not necessarily Sierra Gold Tequila's Tier-1 for spirits.

## 2. Service Level Metrics

Apex tracks four supplier-and-distributor service metrics against the sample tenant service-level agreement:

| Metric | Definition | Sample-Tenant SLA Floor |
| --- | --- | --- |
| Fill Rate | Cases shipped ÷ cases ordered on the same purchase order. | 96% |
| On-Time Delivery | Deliveries arriving inside the delivery window on the purchase order. | 94% |
| Order Accuracy | Line items delivered with no substitutions and no case-count variance. | 97% |
| Damage Rate | Cases arriving damaged ÷ cases shipped. | Below 1.0% |

A distributor falls into an "SLA breach" state when any two of these metrics stay below floor for two consecutive review periods. The distributor's next replenishment run is downgraded from Tier-1 to Tier-2 until the review board clears the breach.

## 3. Fill-Rate Investigation Workflow

When fill rate drops on a specific banner/category slice, the supply chain analyst pulls the following in order:

1. Order coverage — did the purchase order request quantities the distributor could realistically supply for that region and week?
2. Distributor allocation — was Apex's slice of the distributor's regional capacity honored?
3. Substitution log — were substitutes accepted at the receiving door or rejected as short-shipped?
4. Backhaul exceptions — were any deliveries pushed to the next window due to backhaul consolidation?

The analyst records findings against the affected purchase order lines. Findings feed the monthly distributor scorecard.

## 4. Backup Distributor Activation

Backup distributors get activated when the primary distributor's fill rate stays below floor for the banner for three consecutive weeks or when a single event drops fill rate below 80% for one week. Activation is a documented, banner-scoped decision — it never fires globally across all Apex banners at once.

## 5. Distributor Scorecard Grades

Distributor scorecards use a five-band grade:

- **A** — All four service metrics above floor for the review period.
- **B** — One metric below floor, none in breach state.
- **C** — Two metrics below floor, breach clock started.
- **D** — Breach confirmed. Backup distributor activated for affected lines.
- **F** — Sustained breach across two review periods. Contract renegotiation triggered per the sample tenant governance policy.

## 6. Vocabulary

- **Fill Rate** — the primary service-level metric distributors negotiate against.
- **Backhaul** — return trip freight consolidation that can defer delivery windows.
- **Tier-1 / Tier-2** — primary vs. backup distributor status for a banner.
- **SLA Breach** — the escalated state a distributor enters after two review periods below floor on two or more metrics.

## 7. Sample Query Examples

A grounded retrieval hit for this document should surface under the source `apex-supplier-service-levels.md`. Representative questions:

- "What is the Apex supplier fill-rate floor?"
- "How does Apex measure distributor service?"
- "When does Apex activate a backup distributor?"
- "What is the SLA breach workflow at Apex?"
