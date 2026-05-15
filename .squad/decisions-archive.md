# Squad Decisions Archive

Entries archived from decisions.md on 2026-05-14 (entries older than 7 days).

## Archived Decisions

### Telemetry Total Duration (2026-05-04)

- **Context:** The backend 	hought span covers the full GetResponseAsync() wall-clock time, so summing span durations in the web telemetry drawer overstates total request time when tool calls are present.
- **Decision:** Expose TotalDurationMs on the shared ChatResponse contract and have the telemetry drawer prefer that response-level value, with a fallback to summed spans when the response-level value is absent.
- **Impact:** Individual span durations remain visible, the total duration display reflects real request time, and older clients/responses stay compatible through the fallback path.
- **Owner:** Costco (Backend Dev)

### Logo Placement (2026-05-05)

- **Context:** The app needs both a compact brand mark for general navigation chrome and the full shipped logo image for the chat welcome experience.
- **Decision:** BrandLogo.tsx should remain the synthetic RP gradient box plus wordmark component, while /retail-pulse-logo.jpg is used only in ChatPanel.tsx's empty-state welcome area as a centered hero image.
- **Impact:** Shared UI keeps the lighter, scalable brand mark, and the large raster logo is confined to the one place where a full-brand splash is desired.
- **Owner:** Chick (Frontend Dev)


# Squad Decisions Archive

**Archived:** 2026-05-15T19:04:19-04:00

Decisions archived when decisions.md exceeded 51200 bytes (≥7 day retention policy).

---

### Variant-Level Data in SQLite + GetVariantMix Tool (2026-05-07)

- **Context:** The demo failed on variant-level chart requests (e.g., "donut chart of Apex Grill's variant mix in the Southwest") because the SQLite database only stored brand-level metrics. Variant names existed in tenant.yaml but were not queryable.
- **Decision:**
  1. Added `VariantMix` table to the SQLite schema with deterministic seeding from tenant.yaml variant arrays. Mix percentages are normalized random weights per brand×region×variant (seeded via `GetStableHash("variant|{brand}|{region}")`). DepletionsYoY is ±5% range.
  2. Added `GetVariantMix` MCP tool (brand required, region optional/National). National region averages MixPercent and DepletionsYoY across all regions via SQL GROUP BY.
  3. Updated `prompts.yaml`: registered `GetVariantMix` in tools array and Available Tools, added "variant mix / product breakdown / SKU split" → GetVariantMix to the Concept-to-Tool Mapping, and rewrote "Always Chart Available Data" to call GetVariantMix FIRST for variant requests and chart real data directly (no "Estimated" label).
- **Impact:** Variant-level chart requests now resolve to real seeded data. The "Always Chart Available Data" section still handles non-variant estimated breakdowns, but variant queries are now first-class. No breaking changes to existing tools or tables.
- **Owner:** Costco (Backend Dev)

### Tool Enforcement in System Prompt (2026-05-07)

- **Context:** gpt-5.4-mini was responding to data/visualization requests with text-only responses, skipping available tools (GetPortfolioDepletionStats, CreateChart) entirely. The system prompt described tools but never mandated their use.
- **Decision:** Added a "Critical: Always Use Tools for Data Requests" section to `prompts.yaml` that (1) mandates tool calls for all data questions, (2) maps common business concepts to specific tools (e.g., "market share" → GetPortfolioDepletionStats), and (3) maps data types to chart types (e.g., proportional breakdown → pie chart). This section is placed BEFORE the visualization guidelines so the model encounters the mandate early.
- **Impact:** The model should now reliably call data tools first, then CreateChart for visualizations, instead of producing text-only responses. No C# or frontend changes needed — this is prompt engineering only.
- **Owner:** Costco (Backend Dev)