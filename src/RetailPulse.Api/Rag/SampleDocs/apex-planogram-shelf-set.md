# Apex Retail Group — Planogram & Shelf-Set Reference (Sample)

> **Sample corpus notice.** This document is a fictional grounding artifact attributed to the sample tenant "Apex Retail Group." It contains no real customer data, no real supplier data, and no proprietary retailer material. Numbers below illustrate the retrieval pipeline and are not benchmarks.

## 1. Planogram Cadence

Apex banners refresh planograms on a rolling six-week cadence tied to the internal "Shelf-Set Wave" calendar. Every wave carries a Wave ID (for example `Wave-2026-14`) and a scoped set of categories so field teams execute one banner-category slice at a time. Grocery banners (FreshMart, Harvest Table) refresh in odd waves; Spirits banners (Sierra Gold, Ridgeline, Summit Vodka) refresh in even waves; Quick-Serve, Home Improvement, Office Supply, and Furniture banners each roll on their own two-wave cycle.

A shelf-set is considered "live" only after the planogram compliance photo has been uploaded and scored above the acceptance threshold for the banner. Threshold defaults live in the tenant configuration.

## 2. Shelf-Set Compliance Fields

Every shelf-set reset records the following fields into the merchandising data store:

| Field | Description |
| --- | --- |
| `banner_id` | Apex banner (e.g., FreshMart, Sierra Gold Tequila). |
| `store_id` | Apex store identifier. |
| `wave_id` | Shelf-Set Wave label (e.g., `Wave-2026-14`). |
| `set_date` | Date the reset was completed. |
| `compliance_score` | 0-100 acceptance score from the compliance photo. |
| `facings_planned` / `facings_actual` | Product facings from the planogram vs. counted in-store. |
| `void_count` | Empty facings observed during audit. |

A store is flagged for coaching when three consecutive resets land below the banner acceptance floor. Coaching flags never auto-close — the banner merchandising lead has to sign off.

## 3. Category Slotting Guardrails

Apex applies two hard guardrails when slotting a category:

1. **Anchor Placement.** Category anchors (top three velocity SKUs, banner-neutral) must occupy the two most valuable eye-level bays. The category manager cannot slot a new SKU into an anchor position without documented replacement approval.
2. **Adjacent-Set Continuity.** Two related planograms sharing a bay (for example spirits mixers next to Ridgeline Bourbon anchors) must publish resets in the same Wave. Cross-wave adjacency creates orphan facings and drops compliance scores.

## 4. Reset Escalations

When a store misses a scheduled reset date, the workflow escalates in three steps:

1. Store-level task with a 48-hour window.
2. District merchandising manager notification if the task ages out.
3. Banner-level exception review the following Monday if the reset is still open.

Escalations are logged as `planogram.reset_escalation` events. Downstream analytics uses those events to rebuild banner-level reset health without needing raw store operations data.

## 5. Shelf-Set Vocabulary

- **Wave** — the scheduled reset cycle window.
- **Anchor** — the top velocity SKUs a category keeps at eye level.
- **Void** — a facing planned by the planogram but empty at audit time.
- **Continuity Pair** — two planograms that share a bay and reset together.
- **Coaching Flag** — the marker raised when a store misses reset acceptance three cycles in a row.

## 6. Sample Query Examples

A grounded retrieval hit for this document should be recognizable in downstream traces by the source name `apex-planogram-shelf-set.md`. Representative questions that should retrieve from this document include:

- "What is the current Apex shelf-set wave cadence?"
- "How does Apex measure planogram compliance?"
- "When does a store get a coaching flag on shelf resets?"
- "What are anchor placement rules in Apex planograms?"
