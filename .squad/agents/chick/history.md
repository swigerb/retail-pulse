# Chick — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## Learnings

- 2026-05-04T10:20:27.091-04:00 — `src/RetailPulse.Web/src/components/Dashboard.tsx` owns the telemetry drawer layout, and header action buttons should follow the same `margin-right` transition pattern as `chatContainer` so top-right controls stay visible when the drawer opens.
- 2026-05-04T10:20:27.091-04:00 — Frontend verification for this app uses `cd src/RetailPulse.Web && npm run build` and `cd src/RetailPulse.Web && npx vitest run`.
- 2026-05-04T10:32:17.680-04:00 — `src/RetailPulse.Web/src/components/ChartRenderer.tsx` uses shared chart spacing constants so line and bar charts keep x-axis titles clear of bottom legends via extra bottom margin, a neutral x-axis label offset, and legend top padding.
