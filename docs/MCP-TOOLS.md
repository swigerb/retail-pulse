# MCP Tool Reference

The Retail Pulse MCP (Model Context Protocol) Server exposes domain-specific tools that specialist agents invoke via function calling. The MCP server runs at `http://localhost:5200` and is backed by a SQLite database seeded from `tenant.yaml`.

All tools are registered as `[McpServerTool]` classes and are accessible both through the MCP protocol and via proxy REST endpoints on the API server.

---

## Demand Tools

> Namespace: `RetailPulse.McpServer.Tools.DemandTools`

### GetHistoricalDemand

Get historical demand/depletion data aggregated by week.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name (e.g. `"Sierra Gold Tequila"`) |
| `region` | string | | all | Region (e.g. `"Northeast"`) |
| `channel` | string | | all | Channel: `"On-Premise"`, `"Off-Premise"`, `"E-Commerce"` |
| `months` | int | | `12` | Months of history (1–24) |

**Returns:** Weekly aggregated demand data with volume, units, and trend data. Useful for trend analysis and baseline comparisons.

**Example Output:**

```json
{
  "brand": "Sierra Gold Tequila",
  "region": "All",
  "period_months": 12,
  "weekly_data": [
    {
      "week_start": "2026-01-06",
      "volume": 1450.5,
      "units": 2900,
      "channel": "Off-Premise"
    }
  ],
  "total_volume": 75420.0,
  "avg_weekly_volume": 1450.4
}
```

---

### GenerateForecast

Generate a demand forecast using trailing average + seasonal multipliers + trend analysis.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name (e.g. `"Ridgeline Bourbon"`) |
| `region` | string | | all | Region to forecast |
| `days` | int | | `90` | Forecast horizon (7–365 days) |

**Returns:** Daily predicted volume with ±15% confidence bounds and seasonal factor explanations.

**Example Output:**

```json
{
  "brand": "Ridgeline Bourbon",
  "region": "All",
  "forecast_days": 90,
  "daily_forecast": [
    {
      "date": "2026-05-14",
      "predicted_volume": 215.3,
      "lower_bound": 183.0,
      "upper_bound": 247.6,
      "seasonal_factor": 1.12,
      "seasonal_note": "Summer boost"
    }
  ],
  "summary": {
    "avg_daily_volume": 210.5,
    "trend_direction": "up",
    "trend_pct": 3.2
  }
}
```

---

### GetSeasonalityFactors

Get seasonal demand multipliers by month for product categories.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `category` | string | | all | Product category (e.g. `"Spirits"`, `"Grocery"`) |

**Returns:** Monthly multipliers showing which months see boosted or reduced demand, with explanations (holidays, summer, etc.).

**Example Output:**

```json
{
  "category": "Spirits",
  "factors": [
    { "month": 1, "month_name": "January", "multiplier": 0.85, "reason": "Post-holiday lull" },
    { "month": 6, "month_name": "June", "multiplier": 1.15, "reason": "Summer entertaining" },
    { "month": 12, "month_name": "December", "multiplier": 1.45, "reason": "Holiday gifting & parties" }
  ]
}
```

---

### IdentifyDemandRisks

Analyze recent demand data for anomalies and risks over the last 90 days.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand to analyze |
| `region` | string | | all | Region to analyze |

**Returns:** Detected risks ranked by severity (high/medium/low) — sudden drops (>20%), unusual spikes, and trend reversals.

**Example Output:**

```json
{
  "risks": [
    {
      "severity": "high",
      "type": "sudden_drop",
      "brand": "Summit Vodka",
      "region": "Midwest",
      "description": "23% volume decline in weeks 14-16",
      "affected_period": "2026-04-01 to 2026-04-21",
      "recommendation": "Investigate competitive activity and distribution gaps"
    }
  ],
  "total_risks": 3
}
```

---

## Promo Tools

> Namespace: `RetailPulse.McpServer.Tools.PromoTools`

### GetPromoHistory

Get historical promotion campaign data with outcomes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `region` | string | | all | Region |
| `promoType` | string | | all | `"discount"`, `"bogo"`, `"display"`, `"digital"`, `"bundle"` |
| `months` | int | | `18` | Months of history (1–24) |

**Returns:** Campaign records with name, dates, spend, lift%, ROI, and success rating.

**Example Output:**

```json
{
  "campaigns": [
    {
      "campaign_name": "Summer Sizzle 2025",
      "brand": "Sierra Gold Tequila",
      "region": "Southwest",
      "promo_type": "display",
      "start_date": "2025-06-01",
      "end_date": "2025-06-28",
      "spend": 125000,
      "lift_pct": 18.5,
      "roi": 3.2,
      "success_rating": "high"
    }
  ],
  "total_campaigns": 12
}
```

---

### CalculateLift

Estimate expected volume uplift for a promotion.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |
| `region` | string | ✅ | | Region |
| `promoType` | string | ✅ | | Promo type |
| `spend` | double | ✅ | | Planned spend in dollars |

**Returns:** Expected lift percentage with confidence level. Uses historical lift coefficients with diminishing returns for overspend.

**Example Output:**

```json
{
  "brand": "Ridgeline Bourbon",
  "region": "Southwest",
  "promo_type": "discount",
  "spend": 150000,
  "expected_lift_pct": 14.2,
  "confidence": "high",
  "similar_campaigns_count": 8,
  "diminishing_returns_warning": false
}
```

---

### EvaluateTiming

Evaluate the timing of a proposed promotion window.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |
| `region` | string | ✅ | | Region |
| `startDate` | string | ✅ | | ISO date (`2026-06-01`) |
| `endDate` | string | ✅ | | ISO date (`2026-06-28`) |

**Returns:** Timing score (0–1), overlapping campaigns, seasonality boost, and cannibalization risk factors.

**Example Output:**

```json
{
  "timing_score": 0.82,
  "seasonality_boost": 1.15,
  "conflicts": [],
  "risks": [
    { "type": "cannibalization", "detail": "Recent similar promo ended 3 weeks ago" }
  ],
  "recommendation": "Good timing — summer seasonality boost with no conflicts"
}
```

---

### EstimateROI

Full ROI estimation combining lift, timing, and spend effectiveness.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |
| `region` | string | ✅ | | Region |
| `promoType` | string | ✅ | | Promo type |
| `spend` | double | ✅ | | Planned spend |
| `durationWeeks` | int | ✅ | | Duration (1–12 weeks) |

**Returns:** Expected ROI with confidence bounds and breakeven analysis.

**Example Output:**

```json
{
  "expected_roi": 2.85,
  "upper_bound": 3.40,
  "lower_bound": 2.10,
  "confidence": "high",
  "breakeven_spend": 52000,
  "incremental_revenue": 427500
}
```

---

## Competitive Tools

> Namespace: `RetailPulse.McpServer.Tools.CompetitiveTools`

### GetCompetitorPricing

Get current and historical competitor pricing data.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `category` | string | | all | Category (e.g. `"Spirits"`) |
| `region` | string | | all | Region |
| `competitors` | string | | all | Comma-separated competitor names |

**Returns:** Competitor prices, price changes, and aggressive price drops (>10%).

**Example Output:**

```json
{
  "pricing_data": [
    {
      "competitor": "Jack Daniel's",
      "category": "Spirits",
      "current_price": 28.99,
      "previous_price": 31.99,
      "price_change_pct": -9.4,
      "is_aggressive_drop": false,
      "region": "Northeast"
    }
  ]
}
```

---

### GetMarketShare

Get market share trends over time.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `category` | string | | all | Category |
| `region` | string | | all | Region |
| `period` | string | | all | Period (e.g. `"2026-Q1"`) |

**Returns:** Quarterly share data with period-over-period changes. Flags significant share losses (>2 points).

**Example Output:**

```json
{
  "market_share": [
    {
      "brand": "Sierra Gold Tequila",
      "category": "Spirits",
      "period": "2026-Q1",
      "share_pct": 12.5,
      "change_from_prior": 0.8,
      "is_significant_loss": false
    }
  ]
}
```

---

### DetectThreats

Identify competitive threats including price drops, share losses, and high-impact activities.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand to scope |
| `category` | string | | all | Category |
| `region` | string | | all | Region |

**Returns:** Threats ranked by severity with defensive recommendations (`MATCH`, `DIFFERENTIATE`, `PREEMPT`, `IGNORE`).

**Example Output:**

```json
{
  "threats": [
    {
      "severity": "high",
      "type": "aggressive_pricing",
      "competitor": "Patrón",
      "detail": "15% price drop in Southwest region",
      "recommendation": "MATCH",
      "recommended_action": "Consider temporary price match in affected accounts"
    }
  ],
  "total_threats": 4
}
```

---

### GetCompetitiveLandscape

Get a full competitive overview for a category and region.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `category` | string | ✅ | | Category (e.g. `"Spirits"`) |
| `region` | string | ✅ | | Region (e.g. `"Northeast"`) |

**Returns:** Market share positions for all players, recent activities, and pricing moves.

**Example Output:**

```json
{
  "category": "Spirits",
  "region": "Northeast",
  "market_positions": [
    { "brand": "Sierra Gold Tequila", "share_pct": 12.5, "rank": 3 }
  ],
  "recent_activities": [...],
  "pricing_moves": [...]
}
```

---

## Supply Tools

> Namespace: `RetailPulse.McpServer.Tools.SupplyTools`

### GetInventoryLevels

Get current inventory levels by brand, region, and category.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `region` | string | | all | Region |
| `category` | string | | all | Category |
| `status` | string | | all | `"healthy"`, `"low"`, `"critical"`, `"out_of_stock"` |

**Returns:** SKU-level stock, safety stock, days of supply, and status.

**Example Output:**

```json
{
  "inventory": [
    {
      "sku_id": "SKU-001",
      "brand": "Sierra Gold Tequila",
      "region": "Northeast",
      "current_stock": 2400,
      "safety_stock": 500,
      "days_of_supply": 18,
      "status": "healthy"
    }
  ]
}
```

---

### GetSupplyDisruptions

Get active supply chain disruptions.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `region` | string | | all | Region |
| `severity` | string | | all | `"high"`, `"medium"`, `"low"` |
| `activeOnly` | bool | | `true` | Show only active disruptions |

**Returns:** Disruption type (logistics/supplier/weather/demand_surge), severity, impacted SKUs, and estimated resolution.

**Example Output:**

```json
{
  "disruptions": [
    {
      "id": "DISR-001",
      "type": "logistics",
      "severity": "high",
      "brand": "Ridgeline Bourbon",
      "region": "Midwest",
      "description": "Port congestion delaying shipments by 5-7 days",
      "impacted_skus": 12,
      "estimated_resolution": "2026-05-20",
      "is_active": true
    }
  ]
}
```

---

### GetFulfillmentRate

Get order fulfillment rate trends over time.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand name |
| `region` | string | | all | Region |
| `period` | string | | all | Specific period (e.g. `"2026-04"`) |
| `minPeriods` | int | | `6` | Minimum periods to return (1–12) |

**Returns:** Fill rate %, on-time delivery %, and backorder counts by period.

**Example Output:**

```json
{
  "fulfillment": [
    {
      "period": "2026-04",
      "fill_rate_pct": 96.2,
      "on_time_delivery_pct": 93.8,
      "backorder_count": 15
    }
  ]
}
```

---

### GetSupplyHealthSummary

Get aggregate supply chain health summary.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |
| `region` | string | | all | Region |

**Returns:** Overall assessment (Green/Yellow/Red) combining inventory, disruptions, and fulfillment data.

**Example Output:**

```json
{
  "brand": "Sierra Gold Tequila",
  "overall_status": "Yellow",
  "inventory_health": "Green",
  "disruption_risk": "Yellow",
  "fulfillment_trend": "Green",
  "summary": "Minor disruption risk due to 2 active logistics issues",
  "active_disruptions": 2,
  "critical_inventory_skus": 0
}
```

---

## Store Ops Tools

> Namespace: `RetailPulse.McpServer.Tools.StoreOpsTools`

### GetStorePerformance

Get store performance metrics.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `region` | string | | all | Region filter |
| `storeId` | string | | all | Specific store ID (e.g. `"STR-0001"`) |

**Returns:** Revenue vs target, foot traffic, conversion rates. Identifies underperforming stores.

**Example Output:**

```json
{
  "stores": [
    {
      "store_id": "STR-0001",
      "name": "Downtown Flagship",
      "region": "Northeast",
      "revenue": 485000,
      "target": 500000,
      "pct_to_target": 97.0,
      "foot_traffic": 12500,
      "conversion_rate": 0.32,
      "is_underperforming": false
    }
  ]
}
```

---

### GetShelfLayout

Get the current planogram/shelf layout for a specific aisle.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `storeId` | string | ✅ | | Store ID (e.g. `"STR-0001"`) |
| `aisleId` | string | ✅ | | Aisle ID (e.g. `"AISLE-STR-0001-01"`) |

**Returns:** SKU positions, shelf levels, and facing widths.

**Example Output:**

```json
{
  "store_id": "STR-0001",
  "aisle_id": "AISLE-STR-0001-01",
  "total_positions": 48,
  "positions": [
    {
      "position": 1,
      "shelf_level": "eye",
      "sku_id": "SKU-001",
      "brand": "Sierra Gold Tequila",
      "facings": 3,
      "width_inches": 12
    }
  ]
}
```

---

### OptimizePlanogram

Generate an optimized planogram layout for an aisle.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `storeId` | string | ✅ | | Store ID |
| `aisleId` | string | ✅ | | Aisle ID |

**Returns:** Predicted revenue uplift percentage and specific optimization recommendations.

**Example Output:**

```json
{
  "store_id": "STR-0001",
  "aisle_id": "AISLE-STR-0001-01",
  "predicted_uplift_pct": 8.5,
  "recommendations": [
    {
      "type": "move_to_eye_level",
      "sku_id": "SKU-003",
      "from_level": "bottom",
      "to_level": "eye",
      "expected_lift": 12.0
    }
  ]
}
```

---

### PredictStockout

Predict days until stockout for SKUs at a store.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `storeId` | string | ✅ | | Store ID |
| `skuId` | string | | all | Specific SKU to check |

**Returns:** Risk levels (critical/high/medium/low) and current velocity data.

**Example Output:**

```json
{
  "store_id": "STR-0001",
  "predictions": [
    {
      "sku_id": "SKU-005",
      "brand": "Ridgeline Bourbon",
      "current_stock": 12,
      "daily_velocity": 4.2,
      "days_to_stockout": 2.9,
      "risk_level": "critical"
    }
  ]
}
```

---

## Margin Tools

> Namespace: `RetailPulse.McpServer.Tools.MarginTools`

### GetMarginByBrand

Get P&L breakdown by brand.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name (e.g. `"Sierra Gold Tequila"`) |
| `period` | string | | all | Period filter (e.g. `"2026-Q1"`) |

**Returns:** Revenue, COGS, marketing, distribution, and margin percentages.

**Example Output:**

```json
{
  "brand": "Sierra Gold Tequila",
  "periods": [
    {
      "period": "2026-Q1",
      "revenue": 2450000,
      "cogs": 980000,
      "marketing": 245000,
      "distribution": 147000,
      "gross_margin_pct": 60.0,
      "net_margin_pct": 44.0
    }
  ]
}
```

---

### GetMarginDrivers

Identify what's driving margin changes for a brand.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |

**Returns:** Cost categories, impact percentages, and directional trends.

**Example Output:**

```json
{
  "brand": "Sierra Gold Tequila",
  "drivers": [
    {
      "category": "Raw Materials",
      "amount": 45000,
      "impact_pct": 2.1,
      "trend": "increasing",
      "detail": "Agave price increase of 8% YoY"
    }
  ]
}
```

---

### GetMarginTrend

Get margin trajectory over time.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | ✅ | | Brand name |
| `quarters` | int | | `4` | Number of quarters to show |

**Returns:** Gross and net margin trends across quarters.

**Example Output:**

```json
{
  "brand": "Sierra Gold Tequila",
  "trend": [
    { "period": "2025-Q2", "gross_margin_pct": 58.5, "net_margin_pct": 42.1 },
    { "period": "2025-Q3", "gross_margin_pct": 59.2, "net_margin_pct": 43.0 },
    { "period": "2025-Q4", "gross_margin_pct": 60.0, "net_margin_pct": 44.0 },
    { "period": "2026-Q1", "gross_margin_pct": 60.5, "net_margin_pct": 44.2 }
  ],
  "direction": "improving"
}
```

---

### DetectMarginRisks

Identify margin-destructive patterns across brands.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `brand` | string | | all | Brand to scope (omit for all) |

**Returns:** Ranked risks — cost escalation, margin compression, negative net margins — with recommendations.

**Example Output:**

```json
{
  "risks": [
    {
      "severity": "high",
      "type": "cost_escalation",
      "brand": "Ridgeline Bourbon",
      "detail": "Distribution costs up 15% QoQ",
      "impact_on_margin_pct": -2.3,
      "recommendation": "Renegotiate carrier contracts or consolidate shipments"
    }
  ],
  "total_risks": 5
}
```
