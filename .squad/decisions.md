# Squad Decisions

## Active Decisions

### Tool Enforcement in System Prompt (2026-05-07)

- **Context:** gpt-5.4-mini was responding to data/visualization requests with text-only responses, skipping available tools (GetPortfolioDepletionStats, CreateChart) entirely. The system prompt described tools but never mandated their use.
- **Decision:** Added a "Critical: Always Use Tools for Data Requests" section to `prompts.yaml` that (1) mandates tool calls for all data questions, (2) maps common business concepts to specific tools (e.g., "market share" → GetPortfolioDepletionStats), and (3) maps data types to chart types (e.g., proportional breakdown → pie chart). This section is placed BEFORE the visualization guidelines so the model encounters the mandate early.
- **Impact:** The model should now reliably call data tools first, then CreateChart for visualizations, instead of producing text-only responses. No C# or frontend changes needed — this is prompt engineering only.
- **Owner:** Costco (Backend Dev)

### Telemetry Total Duration (2026-05-04)

- **Context:** The backend `thought` span covers the full `GetResponseAsync()` wall-clock time, so summing span durations in the web telemetry drawer overstates total request time when tool calls are present.
- **Decision:** Expose `TotalDurationMs` on the shared `ChatResponse` contract and have the telemetry drawer prefer that response-level value, with a fallback to summed spans when the response-level value is absent.
- **Impact:** Individual span durations remain visible, the total duration display reflects real request time, and older clients/responses stay compatible through the fallback path.
- **Owner:** Costco (Backend Dev)

### Chick Decision — Logo Placement (2026-05-05)

- **Context:** The app needs both a compact brand mark for general navigation chrome and the full shipped logo image for the chat welcome experience.
- **Decision:** `BrandLogo.tsx` should remain the synthetic RP gradient box plus wordmark component, while `/retail-pulse-logo.jpg` is used only in `ChatPanel.tsx`'s empty-state welcome area as a centered hero image.
- **Impact:** Shared UI keeps the lighter, scalable brand mark, and the large raster logo is confined to the one place where a full-brand splash is desired.
- **Owner:** Chick

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
