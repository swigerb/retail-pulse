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
