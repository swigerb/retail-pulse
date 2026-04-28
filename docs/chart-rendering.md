# Chart Rendering Architecture

How Patron Pulse generates and displays data visualizations.

---

## Flow

1. **User asks a question** — e.g., "Show me depletion trends for Patrón Silver"
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
    public string? Color { get; init; }       // hex, e.g. "#8B4513"
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
| Grouped Bar | `groupedBar` | `BarChart` (multi-series) | `Chart.HorizontalBar` |
| Stacked Bar | `stackedBar` | `BarChart` (stacked) | `Chart.HorizontalBar` |
| Horizontal Bar | `horizontalBar` | `BarChart` (horizontal) | `Chart.HorizontalBar` |
| Pie | `pie` | `PieChart` | `Chart.Donut` |
| Donut | `donut` | `PieChart` (inner radius) | `Chart.Donut` |
| Gauge | `gauge` | Custom `PieChart` | `Chart.Donut` (partial) |
| Table | `table` | HTML table | `Table` element |

---

## Bacardi Brand Colors

Default palette applied when no explicit `Color` is set on a series:

| Swatch | Hex | Name |
|--------|-----|------|
| 🟫 | `#8B4513` | Saddle Brown |
| 🟡 | `#DAA520` | Goldenrod |
| 🔵 | `#4682B4` | Steel Blue |
| 🟢 | `#2E8B57` | Sea Green |
| 🟤 | `#CD853F` | Peru |
| 🟠 | `#B8860B` | Dark Goldenrod |
| 🩵 | `#5F9EA0` | Cadet Blue |
| 🧡 | `#D2691E` | Chocolate |

Background: `#111111` · Accent gold: `#C8A951` · Text: `#F5F5F0`

---

## Optional Foundry Agent

The Foundry shipment-analysis agent is **disabled by default** and controlled via configuration:

```json
{
  "FoundryAgent": {
    "Enabled": true,
    "ProjectEndpoint": "https://...",
    "ShipmentAgentName": "Bacardi Shipment Specialist",
    "ShipmentAgentId": "<agent-id>"
  }
}
```

When `FoundryAgent:Enabled` is `false` (default), a local `LocalShipmentAnalyzer` is used instead. This allows the app to run without any Azure AI Foundry dependency.
