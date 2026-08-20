# Apex Retail Group — Merchandising & Promo Execution (Sample)

> **Sample corpus notice.** This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." Promotion mechanics, dates, and thresholds below are illustrative only.

## 1. Promo Windows

Apex runs promotions inside named windows tied to the banner's fiscal calendar. Every promo window has a Window ID (for example `PW-2026-Q1-03`), a scoped list of banners, and a set of participating categories. Windows never span more than a single banner's fiscal period.

## 2. Promo Types and Owners

| Promo Type | Owner | Description |
| --- | --- | --- |
| Feature | Banner Merchandising Lead | Highlighted placement in the weekly ad plus optional endcap. |
| Endcap Display | Store Operations | In-store endcap display, planogram-driven, requires shelf-set continuity. |
| Bundle | Category Manager | Two or more SKUs sold together at a promo price. |
| Threshold Discount | Pricing Lead | Discount applied at basket threshold (spend or unit count). |
| Sample or Demo | Field Marketing | In-store activation with staffed sampling in Apex-approved zones. |

A promo record without an accountable owner does not clear the promo review gate.

## 3. Store-Level Execution Checklist

Store operations validates every promo through five checks before the window opens:

1. **POS Sync** — pricing rules present in the point-of-sale before the first sale of the window.
2. **Signage** — required signage kits printed and hung in the primary and secondary zones.
3. **Endcap Shelf-Set** — endcap facings match the promo planogram; adjacencies preserved.
4. **Inventory Cover** — projected sell-through covers the promo window with the banner's safety buffer.
5. **Team Brief** — store team confirms the daily brief line item that describes the promo.

Missed checks generate `promo.execution_exception` events. Field sentiment surfaces recurring exception patterns to the pod that owns the affected banner.

## 4. Endcap Adjacency Rules

Endcap displays follow Apex adjacency rules to avoid confusing shopper flow:

- Two competing endcaps for the same banner do not run in the same store during overlapping windows.
- Cross-banner endcap adjacencies (for example a Sierra Gold Tequila endcap next to a FreshMart chilled mixers endcap) must publish coordinated planograms in the same Shelf-Set Wave.
- Endcaps required by Feature promos always outrank Endcap Display promos when a store has limited endcap inventory.

## 5. Post-Window Review

Every promo window closes with a two-pass review:

1. **Ops Review** — did the store-level execution checks land inside their SLA windows?
2. **Category Review** — did the promo hit its unit and revenue targets, and did adjacent categories cannibalize or lift?

Both reviews land in the banner post-window packet. Recurring category cannibalization patterns feed the assortment reference.

## 6. Vocabulary

- **Promo Window** — the named calendar slot inside which a promo runs.
- **Endcap Kit** — the signage, planogram, and inventory bundle a store receives before an endcap display goes live.
- **Execution Exception** — a promo check that failed store-level acceptance.
- **Sell-Through Cover** — the ratio of projected available units to promo demand for the window.

## 7. Sample Query Examples

A grounded retrieval hit for this document should surface under the source `apex-merchandising-promo-execution.md`. Representative questions:

- "How does Apex validate promo execution in stores?"
- "What are the Apex endcap adjacency rules?"
- "Who owns a Feature promo at Apex?"
- "What happens after an Apex promo window closes?"
