# Kroger Retail Demo — Availability, Freshness, and Substitutions (Sample)

> Unofficial synthetic demonstration. No affiliation with or endorsement by The Kroger Co. All figures are invented and illustrative; trademarks belong to their owners.

## Metrics

- **On-shelf availability (OSA):** share of tracked items in stock and facing-ready at the start of a day. Synthetic target: 95 percent; watch below 92 percent.
- **Days of supply:** on-hand units divided by trailing average daily units. Perishables flag at fewer than 2 days; center store flags at fewer than 7.
- **Sell-through:** units sold divided by units received over a period.
- **Fulfillment rate:** share of pickup or delivery order lines filled as ordered. Service-level target: 96 percent.

## Substitutions

A substitution is acceptable when it matches size, dietary attributes, and a price within 10 percent of the original. Acceptance rate is accepted substitutions divided by offered substitutions. Policy changes, such as auto-approval of a category, are proposals and require human approval before application.

## Promotion Lift

Lift is promoted units divided by baseline units, minus one. Judge lift only when OSA was at least 92 percent during the promotion; otherwise report the result as availability-constrained. Check for a post-promotion dip over the following two weeks.

## Live Data Gateway and Cache Bypass

Cached price and inventory are acceptable for analysis. Recommend a live lookup (cache bypass) only when cached data older than 15 minutes could change a customer-facing price, substitution, or order promise. Each bypass costs gateway budget; batch lookups and never claim a bypass ran without tool confirmation.
