# Sprint 4 Cleanup Decisions

## Deprecated Legacy Demand Routes (2026-05-13)

- **Context:** The MCP server had duplicate demand endpoints — flat routes (`/api/historical-demand`, `/api/forecast`, `/api/seasonality-factors`, `/api/demand-risks`) alongside the newer namespaced routes (`/api/demand/history`, `/api/demand/forecast`, `/api/demand/seasonality`, `/api/demand/risks`). The API project also had proxy tool classes (`HistoricalDemandTool`, `ForecastTool`, `SeasonalityFactorsTool`, `DemandRisksTool`) that called the legacy flat routes.
- **Decision:** Marked the 4 legacy MCP routes with `X-Deprecated: true` and `Sunset: 2026-12-31` response headers. Assigned unique `WithName` values (`*_Legacy`) to avoid route name conflicts. Marked the 4 API proxy tool classes with `[Obsolete("Use MCP demand tool instead. Will be removed in v2.")]`. Call sites in `Program.cs` suppress CS0618 with `#pragma warning disable` during the transition.
- **Impact:** Legacy routes remain functional for backward compatibility. Consumers should migrate to `/api/demand/*` routes. Full removal planned for v2.
- **Owner:** Costco (Backend Dev)

## Demo Defaults Removed from Contracts (2026-05-13)

- **Context:** `TenantConfiguration.cs` had hardcoded demo defaults (`Company = "Retail Pulse Demo"`, `Industry = "Retail & Consumer Goods"`, theme colors, distribution model "Three-Tier" with default distributor types, `PriceSegment = "Premium"`). These allowed the app to run without a properly configured `tenant.yaml`, masking configuration errors.
- **Decision:** All hardcoded demo defaults removed from the contract classes. Required fields (`company`, `industry`, `brands`, `regions`, `channels`, `distribution.model`, `theme.primaryColor`) are now validated at startup via `FileTenantProvider.Validate()`. Missing any required field throws `InvalidOperationException` with a descriptive message.
- **Impact:** Any deployment must have a fully configured `tenant.yaml`. The app will fail fast on startup if required tenant config is missing. Tests updated to reflect stricter validation.
- **Owner:** Costco (Backend Dev)

## Filtered Exception Logging in Parsers (2026-05-13)

- **Context:** `RetailOpsRouter.ParseClassification()` and `MemoryExtractionService.ParseExtraction()` silently swallowed `JsonException` with no logging, making it impossible to diagnose LLM response format issues in production.
- **Decision:** Added optional `ILogger?` parameter to both static parse methods. On `JsonException`, logs at `Debug` level with structured template: `"Failed to parse {Type}"` plus the exception. Does NOT log the raw JSON payload (security — may contain user data). Instance callers pass their `_logger`; test callers can pass `null` to preserve existing test behavior.
- **Impact:** Parse failures now visible in debug logs without changing method signatures for existing test callers.
- **Owner:** Costco (Backend Dev)
