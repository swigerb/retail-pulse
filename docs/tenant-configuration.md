# Tenant Configuration Guide

Retail Pulse is fully tenant-configurable. Define your company, brands, regions, channels, and theme in a single `tenant.yaml` file and the entire platform adapts — prompts, data, UI, and Teams bot all reflect your tenant.

---

## Quick Start

1. Edit `tenant.yaml` in the repo root
2. Restart the application — all services pick up the new configuration automatically

---

## Schema Reference

```yaml
# Required — your company name
company: "Apex Retail Group"

# Required — industry vertical
industry: "Multi-Category Retail"

# Optional — description used in AI agent prompts
description: "Apex Retail Group is a diversified retail conglomerate with brands spanning spirits, grocery, quick-serve restaurants, home improvement, office supply, and furniture."

# Required — list of brands
brands:
  - name: "Sierra Gold Tequila"       # Display name
    category: "Spirits"               # Product category
    variants:                          # Product variants/SKUs
      - "Blanco"
      - "Reposado"
      - "Añejo"
      - "Extra Añejo"
    priceSegment: "Premium"            # Pricing tier

# Required — geographic regions
regions:
  - "Northeast"
  - "Southeast"
  - "Midwest"

# Optional — sales channels
channels:
  - "On-Premise"
  - "Off-Premise"
  - "E-Commerce"

# Optional — UI theme customization
theme:
  primaryColor: "#1B4D7A"              # Header, primary buttons
  accentColor: "#E8A838"               # Highlights, accents
  logoPath: "assets/apex-logo.png"     # Logo file path
  fontFamily: "Inter, system-ui, sans-serif"

# Optional — distribution model
distribution:
  model: "Three-Tier"
  distributorTypes:
    - "Distributor"
    - "Wholesaler"
    - "Retailer"
```

---

## Field Reference

### Company & Industry

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `company` | string | Yes | Company name displayed in UI and prompts |
| `industry` | string | Yes | Industry vertical (e.g., "Spirits & Beverages") |
| `description` | string | No | Company description injected into AI agent system prompt |

### Brands

Each brand object supports:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | Yes | Brand display name |
| `category` | string | Yes | Product category (e.g., "Tequila", "Vodka") |
| `variants` | string[] | No | Product variants or SKUs |
| `priceSegment` | string | No | Price tier: "Standard", "Premium", "Ultra-Premium" |

### Regions

A flat list of geographic region names. These are used for:
- Data filtering in MCP tools
- AI agent prompt context
- Dashboard region selectors

### Channels

Sales channel names (e.g., "On-Premise", "Off-Premise", "E-Commerce"). Used in data filtering and analytics.

### Theme

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `primaryColor` | hex string | No | Primary brand color for headers and buttons |
| `accentColor` | hex string | No | Accent color for highlights |
| `logoPath` | string | No | Path to logo image |
| `fontFamily` | string | No | CSS font-family value |

The React frontend reads theme values and applies them via CSS custom properties, so the UI automatically matches your branding.

### Distribution

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `model` | string | No | Distribution model name (e.g., "Three-Tier") |
| `distributorTypes` | string[] | No | Types of distribution entities |

---

## Industry Examples

### Multi-Category Retail (included sample)

```yaml
company: "Apex Retail Group"
industry: "Multi-Category Retail"
description: "Apex Retail Group is a diversified retail conglomerate with brands spanning spirits, grocery, quick-serve restaurants, home improvement, office supply, and furniture."
brands:
  # Spirits & Beverages
  - name: "Sierra Gold Tequila"
    category: "Spirits"
    variants: ["Blanco", "Reposado", "Añejo", "Extra Añejo"]
    priceSegment: "Premium"
  - name: "Ridgeline Bourbon"
    category: "Spirits"
    variants: ["Small Batch", "Single Barrel", "Cask Strength"]
    priceSegment: "Premium"
  - name: "Summit Vodka"
    category: "Spirits"
    variants: ["Original", "Citrus", "Pepper"]
    priceSegment: "Premium"
  # Grocery
  - name: "FreshMart"
    category: "Grocery"
    variants: ["Organic Produce", "Bakery", "Deli", "Frozen"]
    priceSegment: "Standard"
  - name: "Harvest Table"
    category: "Grocery"
    variants: ["Fresh Meals", "Meal Kits", "Prepared Foods"]
    priceSegment: "Premium"
  # Quick-Serve Restaurants
  - name: "Apex Grill"
    category: "Quick-Serve Restaurant"
    variants: ["Burgers", "Chicken", "Breakfast", "Beverages"]
    priceSegment: "Standard"
  - name: "Coastline Tacos"
    category: "Quick-Serve Restaurant"
    variants: ["Tacos", "Burritos", "Bowls", "Sides"]
    priceSegment: "Standard"
  # Home Improvement
  - name: "Pinnacle Hardware"
    category: "Home Improvement"
    variants: ["Lumber", "Power Tools", "Paint", "Plumbing"]
    priceSegment: "Standard"
  - name: "Summit Outdoor"
    category: "Home Improvement"
    variants: ["Patio Furniture", "Grills", "Garden", "Landscaping"]
    priceSegment: "Premium"
  # Office Supply
  - name: "ClearDesk"
    category: "Office Supply"
    variants: ["Paper Products", "Ink & Toner", "Technology", "Furniture"]
    priceSegment: "Standard"
  # Furniture
  - name: "Urban Living"
    category: "Furniture"
    variants: ["Living Room", "Bedroom", "Dining", "Outdoor"]
    priceSegment: "Premium"
  - name: "Foundry Home"
    category: "Furniture"
    variants: ["Sofas", "Mattresses", "Desks", "Storage"]
    priceSegment: "Standard"
regions:
  - "Northeast"
  - "Southeast"
  - "Midwest"
  - "Southwest"
  - "West Coast"
  - "Pacific Northwest"
channels:
  - "On-Premise"
  - "Off-Premise"
  - "E-Commerce"
distribution:
  model: "Three-Tier"
  distributorTypes: ["Distributor", "Wholesaler", "Retailer"]
theme:
  primaryColor: "#1B4D7A"
  accentColor: "#E8A838"
```

### Snack Foods

```yaml
company: "Crunch Co."
industry: "Snack Foods"
brands:
  - name: "Crunch Chips"
    category: "Potato Chips"
    variants: ["Classic", "BBQ", "Sour Cream", "Salt & Vinegar"]
    priceSegment: "Standard"
  - name: "Nutty Delights"
    category: "Mixed Nuts"
    variants: ["Roasted", "Honey Glazed", "Spicy"]
    priceSegment: "Premium"
  - name: "Crunch Pretzels"
    category: "Pretzels"
    variants: ["Traditional", "Honey Mustard", "Cheddar"]
    priceSegment: "Standard"
regions:
  - "East Coast"
  - "Central"
  - "West Coast"
channels:
  - "Grocery"
  - "Convenience"
  - "Club Stores"
  - "E-Commerce"
distribution:
  model: "Direct Store Delivery"
  distributorTypes: ["Distributor", "Retailer"]
theme:
  primaryColor: "#D4380D"
  accentColor: "#FFA940"
```

### Craft Beverages

```yaml
company: "Cascade Brewing Collective"
industry: "Craft Beverages"
brands:
  - name: "Cascade IPA"
    category: "Beer"
    variants: ["West Coast IPA", "Hazy IPA", "Session IPA"]
    priceSegment: "Premium"
  - name: "Mountain Cider"
    category: "Hard Cider"
    variants: ["Dry", "Semi-Sweet", "Hopped"]
    priceSegment: "Premium"
  - name: "Cascade Seltzer"
    category: "Hard Seltzer"
    variants: ["Lime", "Grapefruit", "Mango", "Mixed Berry"]
    priceSegment: "Standard"
regions:
  - "Pacific Northwest"
  - "Northern California"
  - "Mountain West"
channels:
  - "Taproom"
  - "On-Premise"
  - "Off-Premise"
distribution:
  model: "Three-Tier"
  distributorTypes: ["Distributor", "Retailer"]
theme:
  primaryColor: "#135200"
  accentColor: "#73D13D"
```

---

## How Tenant Configuration Flows Through the System

1. **Prompts** — `src/RetailPulse.Api/prompts.yaml` uses `{{tenant.*}}` template placeholders. The AI agent's system prompt is automatically populated with your company name, brands, and regions.

2. **Data Seeding** — `RetailPulseDb` seeds a SQLite database with realistic metrics based on the brands and regions defined in `tenant.yaml`. Data is re-seeded automatically when `tenant.yaml` changes.

3. **React UI** — CSS custom properties are set from `theme` values. The dashboard header, sidebar, and charts all reflect your brand colors.

4. **Teams Bot** — Adaptive Card templates use tenant configuration for branding and suggested queries.

---

## Theme Customization

The React frontend applies theme colors via CSS custom properties:

```css
:root {
  --color-primary: var(--tenant-primary, #1B4D7A);
  --color-accent: var(--tenant-accent, #E8A838);
  --font-family: var(--tenant-font, 'Inter, system-ui, sans-serif');
}
```

To see your theme changes:
1. Update the `theme` section in `tenant.yaml`
2. Restart the application
3. The dashboard immediately reflects the new colors

### Color Guidelines

- **Primary color** — Used for headers, navigation, and primary buttons. Choose a dark, readable color.
- **Accent color** — Used for highlights, active states, and charts. Choose a contrasting, vibrant color.
- Ensure sufficient contrast between primary/accent colors and text for accessibility.

---

## Agent Definitions (`prompts.yaml`)

Retail Pulse composes its agent roster from `src/RetailPulse.Api/prompts.yaml`
at startup. Every specialist — routing intents, keyword fast-paths, tool
bindings, council membership, scorecard weight — is declared there. Adding a
new specialist is a config edit plus a restart: no C# class, no DI wiring,
no rebuild. This is the promise formalised in [ADR-008](adr/008-data-driven-agent-definitions.md).

### Schema

Each entry under `agents:` is an `AgentDefinition`. Every field is optional
unless noted; the loader supplies safe defaults.

| Field | Type | Purpose |
|-------|------|---------|
| `name` | string | Display name used in telemetry and logs. |
| `model` | string | Model identifier resolved through the app's model catalog. Defaults to `gpt-4o`. |
| `system_prompt` | string | The prompt sent to the LLM. Tenant tokens (`{tenant.company}`, `{tenant.brands}`, etc.) are hydrated at load time. |
| `temperature` | number | Sampling temperature. Defaults to `0.7`. |
| `tools` | list<string> | Tool names to bind. Each must be registered with `AgentToolRegistry` — an unknown name fails startup with an actionable error. |
| `key` | string | Routing key. Lowercase kebab-case by convention. Defaults to the YAML section name. |
| `display_name` | string | Human-readable label. Falls back to `name`. |
| `role` | string | `"specialist"` (default), `"orchestration"`, or `"router"`. Orchestration entries (router / synth / vote prompts) are not registered as specialists. |
| `intents` | list<string> | Router intents this agent handles. The router's known-intent set is the union of every specialist's list plus the orchestration intents. |
| `keyword_fast_paths` | list<string> | Case-insensitive substrings that force a fast-path route to this agent's primary intent. Use strong, unambiguous phrases only. |
| `fallback_reply` | string | Reply used when the LLM returns empty content. Falls back to a domain-neutral default. |
| `council_participant` | bool | When true, the Portfolio Health Council fans out to this agent. |
| `scorecard_dimension` | string | If set (with a positive `scorecard_weight`) this agent contributes a dimension to the brand scorecard. |
| `scorecard_weight` | number | Weight for the scorecard dimension. Ordered by weight descending in the final report. |
| `prefetchable` | bool | When true, the router calls the agent's `IPrefetchableAgent` hook to warm data before invocation. |
| `use_knowledge_base` | bool | Per-agent RAG toggle. Defaults to `true` so existing agents keep grounding. Set to `false` on orchestration prompts and any agent that must never retrieve — the pipeline short-circuits before any provider call so retrieval adds no latency and no token cost. |
| `knowledge_base_name` | string | Optional logical knowledge source name. When empty (default), retrieval is unscoped over the entire corpus. When set, the value MUST match one of the names declared under `Knowledge:Sources:Named` in configuration — an unknown value aborts startup with the agent key, the unknown name, and the list of valid names. |

### Worked example: adding a Loyalty Analytics specialist

```yaml
agents:
  # ... existing agents ...

  loyalty-analytics:
    name: "Loyalty Analytics Agent"
    model: "gpt-5.4-mini"
    key: "loyalty-analytics"
    display_name: "Loyalty Analytics"
    role: "specialist"
    intents:
      - "loyalty/analytics"
    keyword_fast_paths:
      - "loyalty program"
      - "reward redemption"
    council_participant: false
    fallback_reply: "I couldn't generate a loyalty analytics response."
    system_prompt: |
      You are a Loyalty Analytics specialist for {tenant.company}.
      Analyze reward redemption trends, program enrolment velocity, and
      loyalty-driven revenue lift across {tenant.brands}.
    temperature: 0.2
    tools:
      - GetLoyaltyProgramMetrics
      - CreateChart
```

After a restart the router advertises `loyalty/analytics` in its known intent
set, the keyword fast-paths match the two phrases above, and the specialist
is instantiated as a `ConfiguredSpecialistAgent` — no C# changes required.

### Bespoke agents

A small number of agents ship with hand-written classes because they carry
real behaviour beyond an LLM call: `MemoryManagementAgent` (conversation
memory store) and `CompetitiveIntelAgent` (SignalR alert side effects). Both
still read their `AgentDefinition` from `prompts.yaml` — key, intents, and
keyword fast-paths remain configurable — but the containing class is
deliberately hardcoded because the behaviour cannot be expressed as a prompt.

### Trust boundary

`prompts.yaml` is trusted deployment input: the file is committed alongside
the app or delivered through the same channel as `tenant.yaml`. Retail Pulse
does not currently accept prompt definitions from arbitrary users. Safety
validation of agent definitions (schema, prompt-injection heuristics, tool
allow-listing) is tracked separately in issue #99.

## Per-agent knowledge binding (issue #105)

Retail Pulse binds knowledge sources to specialists in two places:

1. `Knowledge:Sources:Named` in configuration declares logical knowledge
   sources — friendly names mapped to one or more provider `source` values.
2. `use_knowledge_base` / `knowledge_base_name` on each `AgentDefinition`
   binds an agent to a logical source (or opts it out of retrieval).

### Naming knowledge sources in configuration

```jsonc
// appsettings.json
"Knowledge": {
  "Sources": {
    "Named": {
      "planogram":            { "Documents": [ "apex-planogram-shelf-set.md" ] },
      "supplier-service":     { "Documents": [ "apex-supplier-service-levels.md" ] },
      "promo-execution":      { "Documents": [ "apex-merchandising-promo-execution.md" ] },
      "category-assortment":  { "Documents": [ "apex-category-assortment.md" ] }
    }
  }
}
```

Every value in `Documents` must match the `source` string used when the
document was ingested (typically the filename passed to
`IKnowledgeBase.IngestDocumentAsync`). A single logical name can list
multiple documents and multiple agents can bind to the same name — sharing is
intentional so pods that touch the same domain draw from the same corpus.

### Binding a specialist

```yaml
agents:
  planogram:
    name: "Planogram Optimization Agent"
    key: "planogram"
    use_knowledge_base: true
    knowledge_base_name: "planogram"     # matches Knowledge:Sources:Named
    # ... rest of definition

  router:
    name: "Retail Ops Router"
    key: "router"
    use_knowledge_base: false             # orchestration prompt — no retrieval
    # ... rest of definition
```

Rules:

- `use_knowledge_base: false` is a hard skip. `RagContextProvider` returns
  before touching the knowledge provider and before creating a retrieval
  activity, so a disabled agent pays zero latency and zero tokens for
  grounding. The endpoint still captures a `rag.retrieve` trace span
  tagged `retrieval.enabled=false` for observability.
- `knowledge_base_name` accepts only names declared in
  `Knowledge:Sources:Named`. Startup fails loud with a message that
  contains the offending agent key, the unknown name, and the list of
  valid names — no silent typo tolerance.
- Leaving `knowledge_base_name` empty on an enabled agent keeps
  retrieval unscoped (the entire corpus is eligible).
- Retrieved chunks always flow through the existing Content Safety
  indirect-injection path; the grounding block is bounded by the
  ADR-006 tool-context budget (`Budget:ToolResult:MaxResultChars`) so a
  large corpus cannot grow the model's context window unbounded.

### Retrieval telemetry

Every retrieval emits a `rag.retrieve` trace span with `span.type =
retrieval` and the following tags:

| Tag | Description |
|-----|-------------|
| `retrieval.agent_key` | Routing key of the agent that requested retrieval. |
| `retrieval.enabled` | `false` when the agent's binding disabled retrieval. |
| `retrieval.scoped` | `true` when the search was constrained to named sources. |
| `retrieval.source` | Comma-joined named-source values (only when scoped). |
| `retrieval.chunk_count` | Number of chunks kept in the grounding block. |
| `retrieval.duration_ms` | Elapsed retrieval time in milliseconds. |
| `retrieval.budget_trimmed` | Count of chunks dropped by the ADR-006 budget (present only when >0). |

### Sample grounding corpus

The default sample tenant ships with four fictional Apex-attributed documents
covering the four named sources above. They are illustrative and contain no
real customers, suppliers, or proprietary retailer material. Replace them for
a real deployment by editing `KnowledgeBaseSeeder` (in-memory provider) or
ingesting the target documents through the operator API (Azure AI Search
provider). Named-source values in `appsettings.json` must match the `source`
strings actually ingested.
