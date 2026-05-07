# Costco — History

## 2026-04-30 — Team Initialization

- **Project:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail)
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Owner:** Brian Swiger
- **Context:** Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams

## 2026-05-04 — Performance Optimization Session

Agent sessions completed:
- **fix-1-conversation-history**: Added multi-turn support with 10-turn history cap to RetailPulseAgent
- **fix-2-portfolio-tool**: Integrated GetPortfolioDepletionStats MCP tool with API proxy and tests

Both features enable enhanced retail data analysis workflows without breaking changes.

## Session Work — 2026-05-04 Telemetry Accuracy Session

### Fix: Telemetry Total Duration Double-Counting (Commit acbc3d3)
- Issue: Telemetry panel summed all spans including overlapping thought+tool spans, overstating request duration by ~2x
- Root Cause: Backend `thought` span covers full `GetResponseAsync()` wall-clock time; summing all spans counts this twice
- Decision: Expose `TotalDurationMs` on ChatResponse contract; telemetry drawer prefers response-level value with fallback to summed spans
- Changes: Added TotalDurationMs to ChatResponse, wired through Dashboard→TelemetryPanel
- Impact: Wall-clock accuracy improved from ~130.9s misreport to correct ~65.5s
- Validation: Backend build + frontend build + 12 tests pass

## Session Work — 2026-05-07 Prompt Enforcement Session

### Fix: Tool Enforcement in System Prompt (Commit a21cb48)
- Issue: gpt-5.4-mini responded to data/visualization requests with text-only answers, skipping GetPortfolioDepletionStats and CreateChart tools entirely
- Root Cause: System prompt in `prompts.yaml` described tools but never mandated their use for data questions
- Decision: Added "Critical: Always Use Tools for Data Requests" section to `prompts.yaml`, placed BEFORE visualization guidelines so model encounters mandate early
- Changes: 
  - Concept-to-tool mapping table (market share → GetPortfolioDepletionStats, trends → GetDepletionStats, etc.)
  - Visualization selection rules (proportional breakdown → pie chart, trends → line chart, etc.)
  - "Always Chart Available Data" guidance for estimated breakdowns
- Impact: Model now reliably invokes data tools first, then CreateChart for visualizations
- Validation: All 174 backend + 12 frontend tests pass

## Learnings

- 2026-05-04T10:32:17.680-04:00 — The telemetry drawer in `src\RetailPulse.Web\src\components\TelemetryPanel.tsx` should use a response-level wall-clock total, not a sum of span durations, because the backend `thought` span in `src\RetailPulse.Api\Agents\RetailPulseAgent.cs` already includes tool time.
- 2026-05-04T10:32:17.680-04:00 — `src\RetailPulse.Web\src\components\Dashboard.tsx` is the right place to own top-level telemetry stats and pass response metadata from `src\RetailPulse.Web\src\components\ChatPanel.tsx` into the telemetry drawer without changing SignalR span flow.
- 2026-05-04T10:32:17.680-04:00 — Shared chat contract changes for telemetry belong in `src\RetailPulse.Contracts\ChatModels.cs`, with matching frontend shape updates in `src\RetailPulse.Web\src\types\index.ts`.
- 2026-05-04T14:53:22Z — Telemetry accuracy achieved via response-level TotalDurationMs with fallback to span summation for backward compatibility.
- 2026-05-07T15:11:15.222-04:00 — Added "Critical: Always Use Tools for Data Requests" section to `src\RetailPulse.Api\prompts.yaml` to fix gpt-5.4-mini skipping tool calls on data/visualization requests. Includes concept-to-tool mapping table (market share → GetPortfolioDepletionStats, trends → GetDepletionStats, etc.) and visualization selection guidance. Root cause was the system prompt described tools but never mandated their use for data questions.
- 2026-05-07T15:33:55-04:00 — Added `VariantMix` table to SQLite schema in `src\RetailPulse.McpServer\Data\RetailPulseDb.cs`. Schema: Brand, Region, Variant (all COLLATE NOCASE), MixPercent REAL, DepletionsYoY REAL. Primary key is (Brand, Region, Variant). Seeded deterministically using GetStableHash("variant|{brand}|{region}") — normalized random weights per brand×region×variant produce mix percentages summing to ~100%.
- 2026-05-07T15:33:55-04:00 — New MCP tool `GetVariantMixTool` lives in `src\RetailPulse.McpServer\Tools\GetVariantMixTool.cs`. Supports region="National" (averages MixPercent/DepletionsYoY across all regions via GROUP BY). Pattern matches existing tools — static class, `[McpServerToolType]`, inject `RetailPulseDb`, return `data.GetVariantMix(brand, region)`.
- 2026-05-07T15:33:55-04:00 — prompts.yaml update strategy for variant data: add to `tools:` array, `## Available Tools` section, `### Concept-to-Tool Mapping` table, and rewrite `### Always Chart Available Data` to call GetVariantMix first (real data, no "Estimated" label) rather than estimating from brand config.
- 2026-05-07T16:45:21-04:00 — Strengthened variant mix prompt in `src\RetailPulse.Api\prompts.yaml` to prevent model from calling GetDepletionStats/GetFieldSentiment for variant queries. Added explicit FAILED/CORRECT examples and a concrete donut ChartSpec mapping showing how to turn GetVariantMix output (mix_percent values) into a working donut chart (each variant as its own series with one value). Root cause: the model ignored weak "call GetVariantMix" instructions and fell back to familiar tools, then couldn't map unfamiliar output to ChartSpec format.
