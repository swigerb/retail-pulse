---
name: "tool-default-alignment"
description: "Keep agent-facing tool signatures aligned with MCP and REST defaults so model tool calls do not fail before execution"
domain: "backend-api"
confidence: "high"
source: "earned from Apex Grill Southwest performance-tool failure"
---

## Context
Use this skill when a Retail Pulse API tool proxies an MCP or REST endpoint. The model reasons over the proxy signature, not the downstream controller or MCP implementation, so any mismatch in optional vs required parameters can break tool invocation even when the data layer is healthy.

## Patterns
- Mirror optional defaults across all three layers: API proxy tool, MCP tool, and REST endpoint.
- Prefer tenant-real examples in `[Description]` attributes (`Apex Grill`, `Southwest`) so the model sees values that actually exist in `tenant.yaml`.
- When a brand/region query fails, verify the route first (`RetailOpsRouter`), then inspect proxy signatures before blaming the seeded data.
- Add a regression test that omits the optional argument and asserts the generated request still carries the default.

## Examples
- `src\RetailPulse.Api\Tools\DepletionStatsTool.cs` now defaults `period = "YTD"` to match `src\RetailPulse.McpServer\Tools\GetDepletionStatsTool.cs`.
- `src\RetailPulse.McpServer\Program.cs` exposes `/api/depletion-stats` with `string period = "YTD"` so direct HTTP callers behave the same as MCP callers.
- `tests\RetailPulse.Tests\ToolTests.cs` verifies `DepletionStatsTool` sends `period=YTD` when the model omits the argument.

## Anti-Patterns
- Treating a proxy wrapper as a separate contract from the MCP tool it forwards to.
- Requiring parameters in the agent-facing tool that are optional downstream.
- Assuming a missing-answer failure means the tenant data was not seeded.
