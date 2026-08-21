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
