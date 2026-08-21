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
