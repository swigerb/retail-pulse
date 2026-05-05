# Chick — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Session Work — 2026-05-04 Telemetry Accuracy Session

### Fix: Chart X-Axis Label Overlap (Commit a53657f)
- Fixed x-axis "Brand" label overlapping legend in ChartRenderer.tsx
- Added bottom margin, adjusted label offset, legend padding
- Validation: Build + 12 tests pass

### Fix: Header Buttons Slide on Telemetry Open (Commit 9de13cd)
- Dashboard.tsx header buttons now follow same `margin-right` transition as chat container
- Prevents button overlap when telemetry panel opens
- Validation: Build + 12 tests pass

## Learnings

- 2026-05-04T10:20:27.091-04:00 — `src/RetailPulse.Web/src/components/Dashboard.tsx` owns the telemetry drawer layout, and header action buttons should follow the same `margin-right` transition pattern as `chatContainer` so top-right controls stay visible when the drawer opens.
- 2026-05-04T10:20:27.091-04:00 — Frontend verification for this app uses `cd src/RetailPulse.Web && npm run build` and `cd src/RetailPulse.Web && npx vitest run`.
- 2026-05-04T10:32:17.680-04:00 — `src/RetailPulse.Web/src/components/ChartRenderer.tsx` uses shared chart spacing constants so line and bar charts keep x-axis titles clear of bottom legends via extra bottom margin, a neutral x-axis label offset, and legend top padding.
- 2026-05-04T14:53:22Z — Chart responsiveness improved; dashboard header stability confirmed.
- 2026-05-05T13:21:37.378-04:00 — Official brand assets should be mirrored in `docs\` for README usage and `src\RetailPulse.Web\public\` for runtime usage so docs and UI stay visually aligned.
- 2026-05-05T13:21:37.378-04:00 — `src\RetailPulse.Web\src\components\BrandLogo.tsx` now renders the shipped `/retail-pulse-logo.jpg` asset directly, so future brand updates should replace the public image rather than rebuilding a synthetic wordmark in JSX.
- 2026-05-05T15:50:04.362-04:00 — Keep `src\RetailPulse.Web\src\components\BrandLogo.tsx` as the compact RP gradient mark for general UI chrome, and reserve the full `/retail-pulse-logo.jpg` asset for the ChatPanel welcome state hero treatment.
- 2026-05-05T16:40:32.229-04:00 — `src\RetailPulse.Web\src\components\ChatPanel.tsx` welcome hero can swap to alternate shipped assets in `src\RetailPulse.Web\public\`; keep legacy logos alongside new variants instead of replacing existing files when the UI needs both.
