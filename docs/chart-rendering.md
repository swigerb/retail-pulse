# Chart Rendering Architecture

How Retail Pulse generates and displays data visualizations.

---

## Flow

1. **User asks a question** — e.g., "Show me depletion trends for Sierra Gold Tequila"
2. **RetailPulseAgent** fetches data via MCP tools (`GetDepletionStats`, `GetShipmentStats`, etc.)
3. **Agent calls `CreateChart`** — produces a `ChartSpec` JSON describing the chart
4. **Web UI** renders charts with [Recharts](https://recharts.org/) (`ChartRenderer.tsx`)
5. **Teams** renders charts using native Adaptive Card chart elements (`Chart.Donut`, `Chart.HorizontalBar`, etc.)

No server-side image generation is involved — all rendering is client-side.

---

## ChartSpec Model

Defined in `RetailPulse.Contracts/ChartSpec.cs`:

```csharp
public record ChartSpec
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public string? XAxisTitle { get; init; }
    public string? YAxisTitle { get; init; }
    public List<ChartSeries> Data { get; init; } = [];
}

public record ChartSeries
{
    public required string Legend { get; init; }
    public string? Color { get; init; }       // hex, e.g. "#1B4D7A"
    public List<ChartDataPoint> Values { get; init; } = [];
}

public record ChartDataPoint
{
    public required string X { get; init; }    // category / label
    public required double Y { get; init; }    // value
}
```

---

## Supported Chart Types (9)

| Type | Key | Web (Recharts) | Teams (AC Charts) |
|------|-----|----------------|-------------------|
| Line | `line` | `LineChart` | `Chart.Line` |
| Bar | `bar` | `BarChart` | `Chart.HorizontalBar` |
| Grouped Bar | `groupedbar` | `BarChart` (multi-series) | `Chart.HorizontalBar` |
| Stacked Bar | `stackedbar` | `BarChart` (stacked) | `Chart.HorizontalBar` |
| Horizontal Bar | `horizontalbar` | `BarChart` (horizontal) | `Chart.HorizontalBar` |
| Pie | `pie` | `PieChart` | `Chart.Donut` |
| Donut | `donut` | `PieChart` (inner radius) | `Chart.Donut` |
| Gauge | `gauge` | Custom `PieChart` | `Chart.Donut` (partial) |
| Table | `table` | HTML table | `Table` element |

---

## Default Chart Colors

Default palette applied when no explicit `Color` is set on a series (the `BRAND_COLORS`
array in `src/RetailPulse.Web/src/components/ChartRenderer.tsx`) — a blue-forward
sequence with two supporting greens/blue-greens:

| Order | Hex | Name |
|-------|-----|------|
| 1 | `#1565C0` | Primary Blue |
| 2 | `#42A5F5` | Sky Blue |
| 3 | `#4682B4` | Steel Blue |
| 4 | `#2E8B57` | Sea Green |
| 5 | `#1E88E5` | Primary Blue 500 |
| 6 | `#64B5F6` | Light Sky Blue |
| 7 | `#5F9EA0` | Cadet Blue |
| 8 | `#0D47A1` | Deep Blue |

Recharts axes/legend/tooltip pull from a fixed dark-mode set: axis ticks and legend
labels `#A0A0A0`, tooltip background `#1A1A1A` with `rgba(66,165,245,0.3)` border, and
tooltip label `#42A5F5`. The gauge chart's active arc is `#42A5F5`.

The application shell in `src/RetailPulse.Web/src/App.css` defines the semantic CSS
variables the rest of the UI (surfaces, borders, scrollbars, accents) is built from:

| CSS variable | Value |
|--------------|-------|
| `--brand-primary` | `#1565C0` |
| `--brand-primary-light` | `#1E88E5` |
| `--brand-accent` | `#42A5F5` |
| `--brand-accent-light` | `#64B5F6` |
| `--brand-deep-black` | `#080808` |
| `--color-bg` | `#080808` |
| `--color-bg-elevated` | `#0D0D0D` |
| `--color-surface` | `#1A1A1A` |
| `--color-surface-hover` | `#242424` |
| `--color-surface-alt` | `#111111` |
| `--color-text` | `#F5F5F0` |
| `--color-text-muted` | `#A0A0A0` |
| `--color-text-subtle` | `#666666` |

Domain-specific palettes (`FORECAST_COLORS`, `AGENT_ROUTING_CONFIG`, `PROMO_COLORS`,
`SEASONAL_COLORS`, `COUNCIL_COLORS`, `CARD_COLORS`, `OBSERVABILITY_COLORS`,
`STORE_COLORS`, `MARGIN_COLORS`, `SCORECARD_COLORS`) live in
`src/RetailPulse.Web/src/constants/agentRouting.ts` — see [Constants & Theming](#constants--theming) in FRONTEND.md for the full breakdown.

---

## Optional Foundry Agent

The Foundry shipment-analysis agent is **disabled by default** and controlled via configuration:

```json
{
  "FoundryAgent": {
    "Enabled": true,
    "ProjectEndpoint": "https://...",
    "ShipmentAgentName": "Retail Pulse Shipment Specialist",
    "ShipmentAgentId": "<agent-id>"
  }
}
```

When `FoundryAgent:Enabled` is `false` (default), a local `LocalShipmentAnalyzer` is used instead. This allows the app to run without any Azure AI Foundry dependency.
