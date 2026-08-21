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
