### Supply Chain Data Layer + MCP Tools + Council Endpoints (2026-05-13)

- **Context:** Sprint 2.4 requires a supply chain data layer for the new SupplyChainAgent (Kroger building in parallel), plus Portfolio Health Council support endpoints.
- **Decision:**
  1. **Schema v6:** Added three new SQLite tables: `InventoryLevels` (Brand, Region, Category, SKU, CurrentStock, SafetyStock, DaysOfSupply, Status), `SupplyDisruptions` (Brand, Region, DisruptionType, Severity, Description, dates, ImpactedSKUs, IsActive), `FulfillmentRates` (Brand, Region, Period, FillRate, OnTimeRate, BackorderCount). All with COLLATE NOCASE and appropriate indexes.
  2. **Seed data:** ~180 inventory records (60% healthy/20% low/15% critical/5% OOS), 18 active disruptions (logistics 40%/supplier 25%/weather 20%/demand_surge 15%), 6 months × 12 brands × 6 regions fulfillment history with 25% chance of declining trends.
  3. **MCP Tools (SupplyTools.cs):** GetInventoryLevels, GetSupplyDisruptions, GetFulfillmentRate, GetSupplyHealthSummary — follow same `[McpServerToolType]` pattern as DemandTools/CompetitiveTools.
  4. **API Proxy Tools:** InventoryLevelsTool, SupplyDisruptionsTool, FulfillmentRateTool, SupplyHealthTool — follow same HttpClient proxy pattern as existing tools.
  5. **REST endpoints on MCP Server:** `/api/supply/inventory`, `/api/supply/disruptions`, `/api/supply/fulfillment`, `/api/supply/health`
  6. **REST endpoints on API:** Same supply endpoints proxied to MCP, plus `/api/council/convene` (POST), `/api/council/agents` (GET).
  7. **Council convene endpoint:** Returns placeholder CouncilVerdict structure with participant list. ConsensusOrchestrator integration deferred to Kroger's parallel work.
  8. **GetSupplyHealthSummary:** Composite query that aggregates inventory, disruptions, and fulfillment into Green/Yellow/Red assessment per brand/region.
- **Impact:** Kroger can wire SupplyChainAgent tools to these endpoints. Council endpoints ready for ConsensusOrchestrator integration. SchemaVersion bumped to 6 forces re-seed.
- **Owner:** Costco (Backend Dev)
