# Frontend Guide

> Retail Pulse — React dashboard for agentic retail intelligence

## Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| Framework | React | 19.x |
| Build | Vite | 8.x |
| Language | TypeScript | 6.x |
| UI Kit | Fluent UI v9 (`teamsDarkTheme`) | 9.73+ |
| Charts | Recharts | 3.8+ |
| Real-time | SignalR (`@microsoft/signalr`) | 10.x |
| Markdown | ReactMarkdown + remark-gfm | 10.x |
| Testing | Vitest + Testing Library | 4.x |

## Architecture

```
App.tsx
 └─ FluentProvider (teamsDarkTheme)
     └─ ErrorBoundary
         └─ Dashboard ← single page, no router
              ├─ ChatPanel (default view)
              ├─ Feature views (promo, competitive, council, etc.)
              ├─ TelemetryPanel (side drawer)
              └─ Header buttons (view switcher, pending approvals)
```

**Key patterns:**

- **Single dashboard page** — no client-side router; `activeView` state controls which feature panel is shown
- **Real-time via SignalR** — `/hubs/telemetry` hub delivers live spans, approval events, and card updates
- **Component-per-feature** — each domain lives in its own `components/{domain}/` folder with barrel exports
- **Shared API service layer** — `services/*.ts` files wrap all `fetch()` calls with typed responses
- **Centralized constants** — `constants/agentRouting.ts` holds all agent colors, emojis, labels, and domain configs
- **Type-safe contracts** — `types/index.ts` defines all shared interfaces aligned with backend API responses

## Authentication & sign-in (provider-neutral)

The SPA is **provider-neutral**: it renders exactly one sign-in UX chosen at build
time, with all provider logic centralized behind a single interface. See
[ADR-005](adr/005-provider-neutral-authentication.md) and the
[authentication matrix](authentication-matrix.md).

```
VITE_AUTH_MODE (build-time, mirrors backend Authentication__Mode)
   └─ auth/authMode.ts        resolveAuthMode() — pure, fail-closed
        └─ auth/activeProvider.ts   selects ONE SessionProvider + capabilities
             ├─ providers/entraProvider.ts      (MSAL — live, unchanged)
             ├─ providers/githubProvider.ts     (confidential BFF; no provider token in browser)
             └─ providers/anonymousProvider.ts  (limited demo)
                  ├─ AuthGate.tsx → gates/{Entra,GitHub,Anonymous}AuthGate.tsx  (mode UX)
                  ├─ auth/tokenService.ts + auth/authorizedFetch.ts  (all REST + SignalR)
                  └─ session/sessionCredentialStore.ts  (session-only token store)
```

**Build-time mode selection.** `VITE_AUTH_MODE` (`Entra` / `GitHub` / `Anonymous`) is
injected by the deployment (Bicep output → azd env → Vite). `resolveAuthMode()` is
fail-closed: a missing mode resolves to `Entra` only when safe (existing Entra config,
or explicit local-dev pass-through); a missing mode in a production build, or any
unknown/numeric value, **throws at load** — never a silent anonymous default. Only the
configured mode is rendered (no provider chooser).

**One credential path.** A single `SessionProvider` (selected by `activeProvider.ts`)
feeds both `authorizedFetch` (global REST) and the SignalR `accessTokenFactory`;
components never touch provider logic. `authorizedFetch` attaches the token only to our
`/api` paths on an exact-origin match.

**Mode UX (`AuthGate` dispatches to one gate):**

| Mode | Gate label | Flow | Token in browser |
|------|-----------|------|------------------|
| Entra | "Sign in with Microsoft" | MSAL redirect (unchanged) | MSAL token (`sessionStorage`, MSAL-owned) |
| GitHub | "Continue with GitHub" | Top-level nav to `GET /api/auth/github/start` → callback returns one-time code → stripped via `history.replaceState` → `POST /api/auth/github/exchange` | Retail Pulse session token only (**GitHub provider token never in browser**) |
| Anonymous | "Continue in limited demo" | Explicit consent click → `POST /api/auth/anonymous/session` | Short-lived session token only |

**Capabilities.** A build-time capability object per mode centrally hides/disables
surfaces. Anonymous disables the SignalR hubs (`realtimeHub=false` — Dashboard never
starts SignalR, hub token factory returns `''`), Observability, Approvals, Memory,
telemetry, exports, write actions, and alternate views; a consent banner shows the
limitations and a "New anonymous session" action. Entra/GitHub get full capabilities.
This is a **usability layer only** — the backend remains authoritative.

**Session-token storage.** GitHub/Anonymous session tokens live in memory (source of
truth) mirrored to `sessionStorage` for same-tab reload — never `localStorage`, never
a broadly-readable cookie, never cross-tab. Cleared on logout, expiry, and 401/403.

## Dashboard Tabs

The header bar contains toggle buttons that switch the main content area between the chat and specialized dashboards. Click a tab to open it; click again (or "Back to Chat") to return.

**Chat persistence across navigation.** Switching to any tab (Observability, Competitive, etc.) or opening the Approvals overlay does **not** discard the conversation. A single `ChatPanel` instance stays mounted for the lifetime of the Dashboard; when a tab is active the chat host is hidden with `display:none` + `inert` + `aria-hidden` (removed from the tab order and the screen-reader tree) but its React state — messages, charts, session id/history, scroll position, and any in-flight request — is preserved. Returning to Chat restores the exact conversation with no refetch or replay, and a response that resolves while you are on another tab is waiting for you when you come back. **"New Chat" is the only intentional reset** — it remounts the panel (via a `chatKey` bump), clearing all messages/charts and starting a fresh session with the welcome prompts.

| Tab | Icon | What It Shows |
|-----|------|---------------|
| Approvals | Fluent UI `Badge` (count) | Pending human-in-the-loop approvals for promotions and campaigns |
| Campaign Planner | `TargetArrow24Regular` | Promotion planning workspace with ROI modeling and approval gates |
| Competitive | `Shield24Regular` | Competitive intelligence — threats, market share, pricing alerts |
| Knowledge Base | `Library24Regular` | RAG-indexed documents the agent uses for grounded answers |
| Health Council | `HeartPulse24Regular` | Multi-agent consensus scoring for brand/portfolio health |
| Security | `ShieldCheckmark24Regular` | Guardrails dashboard — blocked requests, PII redaction, jailbreak stats |
| Cards | `CardUi24Regular` | Adaptive Cards for structured agent responses (voting, sign-offs) |
| Observability | `Eye24Regular` | Token usage, cost tracking, audit log, conversation export |
| Stores | `Building24Regular` | Store operations — heatmap, stockout risks, planograms, performance |
| Financials | `Money24Regular` | P&L waterfall chart and margin driver analysis |
| Portfolio | `Star24Regular` | Portfolio Scorecard — weighted brand scoring with drill-down explainability |

Icons are imported from `@fluentui/react-icons`; the "New Chat" button uses
`Add24Regular` and the telemetry drawer toggle uses `DataUsage24Regular` (open) /
`Dismiss24Regular` (close).

### Tab Details

**Approvals** — Shows a badge count in the header when approvals are pending. Click to open the telemetry drawer and review/approve/reject promotion proposals the agent generated. Approvals arrive in real time via SignalR.

**Campaign Planner** — A full-screen workspace for building promotional campaigns. Enter campaign parameters (target audience, discount %, duration) and the agent models expected ROI, cannibalization risk, and recommends a go/no-go. Requires human approval before execution. *(Demo Act 7)*

**Competitive** — Displays competitor threat cards, market share trends, pricing comparison grids, and real-time pricing alerts. The agent monitors competitor activity and surfaces actionable intelligence. *(Demo Act 8)*

**Knowledge Base** — Browse and search the document corpus the agent draws from when answering questions. Shows indexed documents, relevance scores, and lets you verify what sources the agent used. Useful for grounding audits.

**Health Council** — A panel of specialist AI agents (demand, margin, competitive, supply) each vote on brand health. The council produces a consensus score with per-dimension breakdowns. Click a brand to see individual agent votes and reasoning. *(Demo Act 9)*

**Security** — Real-time guardrails monitoring: total blocked requests, jailbreak attempt count, PII detections, and access denials. Includes a configuration panel to adjust sensitivity levels and view block-rate timelines. *(Demo Act 10)*

**Cards** — Interactive Adaptive Cards that agents send for structured collaboration. Cards support voting (thumbs up/down), structured data display, and multi-step wizards. Used for team decisions and approval workflows.

**Observability** — Enterprise monitoring suite showing token consumption, cost per conversation, model usage breakdown, and a full audit trail. Includes conversation export (JSON) for compliance. *(Demo Act 10)*

**Stores** — Store operations dashboard with a geographic heatmap (color-coded by performance), stockout risk alerts, before/after planogram comparisons, and a sortable performance table.

**Financials** — Visualizes the P&L as a waterfall chart (revenue → COGS → gross margin → opex → net) and shows margin drivers ranked by impact. Useful for understanding where margin is gained or lost.

**Portfolio** — Multi-dimensional brand scorecard weighting demand, margin, competitive position, and supply chain health. Click any brand for a detailed score card. Click "Why?" on any score to see the agent's decision explanation with tool calls and sources cited. *(Demo Act 9)*

## Directory Layout

```
src/RetailPulse.Web/src/
├── App.tsx                    # Root: FluentProvider + ErrorBoundary + Dashboard
├── components/
│   ├── Dashboard.tsx          # App shell & view orchestrator
│   ├── ChatPanel.tsx          # Main chat interface
│   ├── ChartRenderer.tsx      # Polymorphic chart renderer
│   ├── TelemetryPanel.tsx     # Live telemetry drawer
│   ├── SpanTimeline.tsx       # Span visualization
│   ├── ErrorBoundary.tsx      # Error catch boundary
│   ├── BrandLogo.tsx          # Compact RP brand mark
│   ├── AgentRoutingIndicator.tsx  # Per-message routing pill
│   ├── AgentRoutingPanel.tsx  # Routing statistics widget
│   ├── ApprovalCard.tsx       # Inline approval card
│   ├── ApprovalHistory.tsx    # Past approvals table
│   ├── PendingApprovals.tsx   # Header badge + dropdown
│   ├── MemoryIndicator.tsx    # Per-message memory chip
│   ├── MemoryPanel.tsx        # Full memory manager
│   ├── alerts/                # Alert feed, cards, history
│   ├── cards/                 # Collaborative adaptive cards
│   ├── competitive/           # Competitive intelligence
│   ├── council/               # Multi-agent health council
│   ├── forecast/              # Demand forecasting charts
│   ├── guardrails/            # Safety & PII redaction
│   ├── knowledge/             # RAG knowledge base
│   ├── margin/                # Margin waterfall & escalation
│   ├── observability/         # Cost, audit, export
│   ├── promo/                 # Promotion planning
│   ├── scorecard/             # Portfolio scorecard & explainability
│   ├── stores/                # Store ops & planograms
│   ├── streaming/             # Token streaming & cache
│   └── traces/                # Distributed trace viewer
├── constants/
│   └── agentRouting.ts        # Colors, emojis, domain configs
├── services/                  # API service layer
│   ├── api.ts                 # Core chat API
│   ├── approvalApi.ts         # Approval endpoints
│   ├── cardsApi.ts            # Adaptive cards endpoints
│   ├── competitiveApi.ts      # Competitive intel endpoints
│   ├── councilApi.ts          # Council endpoints
│   ├── guardrailsApi.ts       # Guardrails endpoints
│   ├── knowledgeApi.ts        # Knowledge base endpoints
│   ├── marginApi.ts           # Margin endpoints
│   ├── memoryApi.ts           # Memory endpoints
│   ├── observabilityApi.ts    # Observability endpoints
│   ├── promoApi.ts            # Promotion endpoints
│   ├── scorecardApi.ts        # Scorecard endpoints
│   ├── storeApi.ts            # Store operations endpoints
│   └── telemetryHub.ts        # SignalR hub connection
└── types/
    └── index.ts               # All shared TypeScript interfaces
```

---

## Component Catalog

### Core Shell

#### `Dashboard`
The top-level app shell. Manages the `activeView` state (`'chat' | 'promo' | 'competitive' | 'knowledge' | 'council' | 'cards' | 'observability' | 'security' | 'stores' | 'financials' | 'portfolio'`), wires the telemetry drawer, and connects to the SignalR hub for live updates.

- **State:** `telemetryOpen`, `activeView`, `connected`, `liveSpans`, `totalDurationMs`, `totalTokenUsage`, `routingHistory`, `pendingApprovals`, `approvalHistory`, `alerts`, `traces`, `selectedBrand`, `explanationOpen`, `explanationData`
- **Services:** `connectTelemetryHub()` from `telemetryHub.ts`
- **Renders:** `ChatPanel`, `TelemetryPanel`, `AgentRoutingPanel`, `MemoryPanel`, `ApprovalHistory`, `PendingApprovals`, plus all feature views
- **Persistent chat host:** `ChatPanel` is rendered once inside a wrapper (`data-testid="chat-host"`) that is hidden with `display:none` + `inert` + `aria-hidden` whenever `activeView !== 'chat'` (or an alternate view is otherwise active), instead of being unmounted. This keeps the conversation, session id, charts, and pending requests alive across navigation. Only `handleNewChat` (the "New Chat" button) resets it, by incrementing `chatKey` to force a remount.

#### `ErrorBoundary`
React error boundary that catches render errors and shows a fallback UI with a retry button.

- **Props:** `children`, optional `fallback` render prop
- **State:** `hasError`, `error`

#### `BrandLogo`
Compact gradient "RP" mark with optional "RETAIL PULSE" wordmark. Used in the header chrome (not the full logo image).

- **Props:** `size`, `className`, `showWordmark`

### Chat & Streaming

#### `ChatPanel`
The main chat interface. Sends user messages to the backend, renders assistant replies with markdown, charts, routing indicators, memory chips, approval cards, and streaming tokens. Handles welcome state with hero logo.

- **State:** `messages`, `input`, `loading`, `streamingTokens`, `isStreaming`, `sessionId`, `selectedCategory`
- **Services:** `sendMessage()` → `POST /api/chat`; `joinTelemetrySession()` via SignalR
- **Renders:** `ChartRenderer`, `AgentRoutingIndicator`, `MemoryIndicator`, `ApprovalCard`, `StreamingMessage`, `BlockedRequestMessage`, `CacheIndicator`

#### `StreamingMessage` — `streaming/`
Progressive token display with typing cursor animation. Renders accumulated tokens as markdown using ReactMarkdown.

- **Props:** `tokens: StreamingToken[]`, `isStreaming: boolean`, `onComplete?: () => void`

#### `CacheIndicator` — `streaming/`
Inline "⚡ Cached" pill badge with tooltip showing time saved and TTL remaining.

- **Props:** `cacheInfo: CacheInfo`

#### `ChartRenderer`
Polymorphic chart renderer — takes a `ChartSpec` array and dispatches on the spec's
`type` field to render one of nine supported chart shapes via Recharts. Also supports
the forecast chart variant when `forecastData` is provided.

- **Props:** `charts: ChartSpec[]`, `forecastData?: ForecastData`
- **Chart types (9):** `line`, `bar`, `groupedbar`, `stackedbar`, `horizontalbar`, `pie`, `donut`, `gauge`, `table`

### Agent Routing

#### `AgentRoutingIndicator`
Subtle color-coded pill shown per-message in chat. Displays the routed agent name, confidence bar, and expandable reasoning text.

- **Props:** `routing: RoutingInfo`
- **State:** `expanded` (reasoning toggle)
- **Colors:** From `AGENT_ROUTING_CONFIG` in `agentRouting.ts`

#### `AgentRoutingPanel`
Statistics widget displayed in the telemetry drawer. Shows total query count, average confidence, fallback rate, and per-intent category bar charts.

- **Props:** `routingHistory: RoutingInfo[]`
- **Derived:** `stats`, `avgConfidence`, `fallbackRate`

### Demand Forecasting — `forecast/`

#### `ForecastChart`
Main composed chart showing historical actuals (solid blue line), predicted values (dashed violet line), confidence band (gradient fill area), seasonal annotations (`ReferenceArea`), and a "Today" divider line.

- **Props:** `data: ForecastData`
- **Library:** Recharts `ComposedChart`

#### `ForecastSummary`
KPI strip above the forecast chart: current average, forecast average, trend direction/percentage, and top seasonal factor.

- **Props:** `data: ForecastData`

#### `DemandRiskCards`
Expandable risk cards sorted by severity (🔴 high → 🟡 medium → 🟢 low). Click to expand detail text. Keyboard accessible.

- **Props:** `data: ForecastData`

### Promotion Planning — `promo/`

#### `PromoTaskModule`
Full promotion planning form. Users select a promo type, enter budget/dates, and the form evaluates the campaign via the API. Shows recommendation, calendar, and ROI chart.

- **State:** form fields, `evaluation`, `campaigns`, `loading`
- **Services:** `evaluatePromo()` → `POST /api/taskmodule/promo`; `fetchExistingCampaigns()` → `GET /api/campaigns`; `submitForApproval()` → `POST /api/taskmodule/promo/submit`

#### `PromoRecommendation`
Displays the AI evaluation verdict (approve/caution/reject), ROI band, risk details, and an optional "Submit for Approval" action.

- **Props:** `evaluation: PromoEvaluation`, `budget: number`, `onSubmitForApproval?: () => void`

#### `PromoCalendar`
Timeline view of existing and proposed campaigns. Shows overlaps and scheduling conflicts visually.

- **Props:** `campaigns: PromoCampaign[]`, `proposedCampaign?: PromoCampaign`

#### `ROIChart`
ROI comparison chart benchmarking the proposed campaign against historical performance by promo type.

- **Props:** proposed/historical ROI values, `promoType: string`

#### `PromoTypeSelector`
Promo type picker with optional historical ROI breakdown per type.

- **Props:** `value: string`, `onChange: (type: string) => void`, `historicalRoi?: Record<string, number>`

### Competitive Intelligence — `competitive/`

#### `CompetitiveDashboard`
Main competitive intelligence view with tabs, category/region filters, and sections for pricing, market share, threats, and competitor profiles.

- **State:** `category`, `region`, active tab, fetched datasets
- **Services:** `fetchCompetitorPricing()` → `GET /api/competitive/pricing`; `fetchMarketShare()` → `GET /api/competitive/market-share`; `fetchThreats()` → `GET /api/competitive/threats`; `fetchCompetitorProfile()` → `GET /api/competitive/competitor/{name}`

#### `PricingGrid`
Tabular + chart view of competitor pricing data. Highlights price gaps and trends.

- **Props:** `data: CompetitorPricing[]`

#### `MarketShareChart`
Recharts area chart showing market share trends by competitor over time. Supports compact mode.

- **Props:** `data: MarketShareEntry[]`, `compact?: boolean`

#### `ThreatCards`
Competitive threat alert cards with severity indicators. Each card can trigger an AI-generated response plan.

- **Props:** `threats: CompetitiveThreat[]`, `compact?: boolean`, `onViewCompetitor?: (name: string) => void`
- **Services:** `generateResponsePlan()` → `POST /api/competitive/threats/{threatId}/response-plan`

#### `CompetitorProfile`
Modal-style detail panel for a single competitor with charts and a close button.

- **Props:** `competitor: CompetitorOverview`, `onClose: () => void`

### Knowledge Base — `knowledge/`

#### `KnowledgeBasePanel`
RAG document manager with search, upload, delete, and stats sections.

- **State:** `docs`, `query`, `results`, `loading`
- **Services:** `fetchDocuments()` → `GET /api/knowledge/documents`; `deleteDocument()` → `DELETE /api/knowledge/documents/{id}`; `searchKnowledgeBase()` → `POST /api/knowledge/search` (JSON body)

#### `DocumentUpload`
Drag-and-drop upload interface for `.md` and `.txt` documents.

- **Props:** `onUploadComplete: () => void`
- **Services:** `uploadDocument()` → `POST /api/knowledge/upload` (JSON body)

#### `CitationBadge`
Inline citation pill with hover tooltip previewing the source document passage.

- **Props:** `citation: { source: string; passage: string }`

#### `SearchResults`
Rendered list of KB search hits as clickable result cards with relevance scores.

- **Props:** `results: KBSearchResult[]`, `query: string`

#### `KnowledgeStats`
Fetches and displays KB metrics (document count, chunk count, index health).

- **Services:** `fetchKBStats()` → `GET /api/knowledge/stats`

### Council (Multi-Agent Consensus) — `council/`

#### `CouncilPanel`
Orchestrator for the council workflow: idle → convening → voting → verdict. Users select a brand/region, convene the council, and watch specialist agents deliberate.

- **State:** phase, selections, result
- **Services:** `conveneCouncil()` → `POST /api/council/convene`

#### `CouncilVoting`
Displays specialist agent votes by domain with loading state indicators.

- **Props:** `votes: CouncilAgentVote[]`, `loading?: boolean`

#### `VoteCard`
Individual agent vote card showing domain icon, health rating (🟢🟡🔴), confidence, and reasoning.

- **Props:** `vote: CouncilAgentVote`, `index: number`, `animate?: boolean`

#### `CouncilVerdict`
Synthesized verdict card with overall health rating, top priorities, and links to disagreement details.

- **Props:** `verdict: CouncilVerdict`

#### `DisagreementHighlight`
Displays conflicting positions by topic/domain when agents disagree.

- **Props:** `disagreements: CouncilDisagreement[]`

#### `CouncilHistory`
Past council sessions list with expandable details per session.

- **State:** expand/collapse, history data
- **Services:** `fetchCouncilHistory()` → `GET /api/council/history`

### Collaborative Cards — `cards/`

#### `AdaptiveCardPanel`
Container that fetches active adaptive cards and subscribes to SignalR for live updates. Renders voting or drill-down cards with lifecycle indicators.

- **State:** `loading`, `cards`, `drillDownLevels`, `userVotes`, `connection`
- **Services:** `fetchActiveCards()` → `GET /api/cards`; `submitVote()` → `POST /api/cards/{cardId}/vote`; SignalR card events

#### `VotingCard`
Multi-user voting card with approve/reject/abstain buttons, vote tally bar, split-vote detection, and escalation triggers.

- **Props:** `card: AdaptiveCard`, `currentUserId: string`, `onVote: (cardId, choice) => void`

#### `DrillDownCard`
Hierarchical card with breadcrumb navigation for drilling into detail levels.

- **Props:** `card: AdaptiveCard`, `levels: DrillDownLevel[]`

#### `CardComments`
Inline comment thread on a card. Supports adding new comments.

- **Props:** `cardId: string`, `comments: CardComment[]`
- **Services:** `addComment()` → `POST /api/cards/{cardId}/comments`

#### `CardLifecycleIndicator`
Horizontal stepper showing the card's state machine (Draft → Active → Voting → Resolved → Archived).

- **Props:** `state: CardLifecycleState`
- **Colors:** `CARD_LIFECYCLE_CONFIG` from `agentRouting.ts`

#### `EscalationBanner`
Amber notification banner shown when a card triggers escalation (e.g., split vote threshold exceeded).

- **Props:** `card: AdaptiveCard`

### Store Operations — `stores/`

#### `StoreHeatmap`
Region-grouped performance grid with color-coded cells (green=strong, red=underperforming). Clickable cells for store drill-down.

- **Props:** `stores: StorePerformance[]`, `onStoreClick?: (storeId: string) => void`

#### `PlanogramDiagram`
Visual shelf layout with eye-level product highlights. Supports before/after comparison mode.

- **Props:** `before: PlanogramLayout`, `after: PlanogramLayout`, `comparisonMode?: boolean`

#### `StockoutAlert`
Urgency-sorted stockout risk cards with severity color-coding (red/amber/green).

- **Props:** `risks: StockoutRisk[]`

#### `StorePerformanceTable`
Sortable ranked store performance table with clickable rows for drill-down.

- **Props:** `stores: StorePerformance[]`, `onStoreClick?: (storeId: string) => void`

### Margin & Financials — `margin/`

#### `MarginWaterfall`
Recharts stacked-bar waterfall chart for P&L margin decomposition. Supports comparison overlay at 40% opacity.

- **Props:** `steps: MarginWaterfallStep[]`, `title?: string`, `comparisonSteps?: MarginWaterfallStep[]`
- **Pattern:** Running totals with transparent `base` bar + colored `value` bar

#### `MarginDrivers`
Horizontal impact bars showing margin drivers ranked by magnitude, with trend arrows (↑↓→).

- **Props:** `drivers: MarginDriver[]`

#### `EscalationPath`
Collapsible vertical timeline showing L1 → L2 → L3 escalation steps with pulse animation on active step.

- **Props:** `steps: EscalationStep[]`, `defaultExpanded?: boolean`
- **Services:** Data loaded via `fetchEscalationPath()` → `GET /api/escalation/{traceId}`

### Portfolio Scorecard — `scorecard/`

#### `PortfolioScorecard`
Grid of brand score cards with SVG score rings and skeleton loading state. Shows generation time when available.

- **Props:** `brands: BrandScore[]`, `loading?: boolean`, `generationTimeMs?: number`, `onBrandClick?: (name) => void`, `onWhyClick?: (traceId) => void`
- **Services:** Data loaded via `fetchPortfolioScorecard()` → `GET /api/portfolio/scorecard`

#### `BrandScoreCard`
Detailed single-brand view with Recharts `RadarChart` for multi-dimension scoring and dimension progress bars.

- **Props:** `brand: BrandScore`, `onWhyClick?: (traceId) => void`
- **Services:** Data loaded via `fetchBrandScore()` → `GET /api/portfolio/brand/{brandName}`

#### `ExplanationPanel`
Slide-out overlay with staggered step reveal animation showing the AI's reasoning chain for a decision.

- **Props:** `explanation: ExplanationData`, `open: boolean`, `onClose: () => void`
- **Services:** Data loaded via `fetchExplanation()` → `GET /api/explain/{traceId}`

#### `WhyButton`
Reusable purple "?" button that triggers explainability lookups. Shows loading spinner during fetch.

- **Props:** `traceId?: string`, `onClick?: () => void`, `loading?: boolean`, `size?: 'small' | 'medium'`

### Guardrails — `guardrails/`

#### `BlockedRequestMessage`
Friendly amber shield UI shown inline in chat when a request is blocked by guardrails. Offers a rephrase suggestion.

- **Props:** `reason: string`, `suggestion?: string`

#### `GuardrailsDashboard`
Admin dashboard with stats cards, trend chart, and filtered list of guardrail detections.

- **State:** `stats`, `loading`, filter state
- **Services:** `fetchGuardrailsStats()` → `GET /api/guardrails/stats`

#### `PiiRedactionBadge`
Inline badge for `[REDACTED:type]` markers in text. Includes `renderWithRedactions()` parser utility for replacing markers with badges.

- **Props:** `redactionType: PiiRedactionType`

#### `GuardrailsConfig`
Admin configuration panel with toggles and pattern editors for guardrail rules. Supports save and reset.

- **State:** `config`, `loading`, toggle states
- **Services:** `fetchGuardrailsConfig()` → `GET /api/guardrails/config`; `updateGuardrailsConfig()` → `PUT /api/guardrails/config`; `resetGuardrailsConfig()` → `POST /api/guardrails/config/reset`

### Observability — `observability/`

#### `ObservabilityPanel`
Tabbed container for the three observability views: Cost Dashboard, Audit Log, and Conversation Export.

- **State:** `activeTab`

#### `CostDashboard`
Token usage and cost charts with a **Today / This Week / This Month** period selector.
Shows metric summary cards, trend area/line chart, agent cost breakdown bar chart, and
tool usage table.

- **State:** `selectedPeriod`, `data`, `loading`
- **Services:** `fetchCostDashboard(period)` fans out to four `/api/observability` endpoints in parallel: `GET /api/observability/costs?period=`, `GET /api/observability/costs/agents?period=`, `GET /api/observability/costs/trend?days=`, `GET /api/observability/costs/tools?period=`. The summary endpoint is required; the other three fall back to empty sections on 404 so partial telemetry still renders.

#### `AuditLogViewer`
Filterable, paginated audit log table with expandable detail rows. Supports text search, action type, and date range filters.

- **State:** `filters`, `page`, `query`, `loading`
- **Services:** `fetchAuditLog()` → `GET /api/observability/audit`

#### `ConversationExport`
Session list with markdown/JSON export options. Includes preview modal before download.

- **Services:** `fetchExportSessions()` → `GET /api/observability/export/sessions`; `fetchExportPreview()` → `GET /api/observability/export/{sessionId}/preview`; `exportSession()` → `POST /api/observability/export/{sessionId}` (returns `Blob`)

### Alerts & Approvals

#### `AlertCard` — `alerts/`
Single alert card with severity styling and dismiss/snooze/view-details actions. Supports auto-dismiss and entry animation.

- **Props:** `alert`, `onDismiss`, `onSnooze`, `onViewDetails`, `autoDismissMs?`, `animate?`

#### `AlertFeed` — `alerts/`
Sorted/grouped alert list with clear-all action and empty state.

- **Props:** `alerts`, `onDismiss`, `onSnooze`, `onClearAll`

#### `AlertHistory` — `alerts/`
Searchable/filterable table of past alert records.

- **Props:** `alerts`

#### `ApprovalCard`
Inline approval card shown in chat with urgency color-coding (red/yellow/green), countdown timer, reasoning/impact section, and approve/reject/modify buttons. Transitions to resolved state with decision banner.

- **Props:** `approval: ApprovalRequest`, `onResolved?: () => void`
- **Services:** `respondToApproval()` → `POST /api/approvals/{id}/respond`

#### `ApprovalHistory`
Filterable/searchable table of all approval records with status indicators.

- **Props:** `approvals: ApprovalRequest[]`
- **State:** `search`, `decisionFilter`

#### `PendingApprovals`
Header toolbar button with live pending-count badge and pulse animation on new arrivals. Can poll independently or receive count from parent.

- **Props:** `pendingCount?`, `pendingApprovals?`, `onClick?`
- **Services:** `fetchPendingApprovals()` → `GET /api/approvals/pending`

### Memory — Root Components

#### `MemoryIndicator`
Subtle violet-themed chip shown per-message in chat (below the routing pill). Tooltip lists memory entries used in context.

- **Props:** `memoryContext: MemoryContext`
- **State:** `isOpen` (tooltip toggle)

#### `MemoryPanel`
Full memory management widget in the telemetry drawer. Search, filter by type, forget individual entries or forget all.

- **Props:** optional `entries: MemoryEntry[]`
- **State:** `entries`, `loading`, `error`, `search`, `typeFilter`
- **Services:** `fetchMemories()` → `GET /api/memory`; `deleteMemory()` → `DELETE /api/memory/{id}`; `deleteAllMemories()` → `DELETE /api/memory`

### Traces — `traces/`

#### `TraceTimeline`
Detailed span-by-span timeline for a single trace with type-specific icons and colors.

- **Props:** `trace`

#### `TraceCard`
Collapsible summary card for a trace. Expands to reveal the full `TraceTimeline`.

- **Props:** `trace`

#### `TraceDashboard`
Trace list with summary statistics and embedded trace timelines. Limits display count.

- **Props:** `traces`, `maxDisplay?: number`

### Telemetry

#### `TelemetryPanel`
Side drawer showing live telemetry: connection status, total duration, token count, and the `SpanTimeline`.

- **Props:** `connected?`, `liveSpans`, `totalDurationMs?`, `totalTokenUsage?`, `onClear`

#### `SpanTimeline`
Visual list of telemetry spans with type icon, duration bar, token count, timestamp, and detail text.

- **Props:** `spans: AgentSpan[]`

---

## API Service Layer

All services live in `src/services/` and wrap `fetch()` calls with typed responses. Base URL resolution is centralized in `src/config/apiOrigin.ts` and `src/config/telemetryHubUrl.ts`, and is deliberately **different for REST vs SignalR**:

- **REST `/api/**`** — services call same-origin paths (e.g. `fetch('/api/chat')`) and pass them through `resolveApiUrl(path)`. `resolveApiUrl` returns the path unchanged unless `VITE_API_ORIGIN` is set to a bare HTTP(S) origin at build time, in which case it prefixes that origin. This lets a single build cover three deploy topologies:
  - **Vite dev** (`npm run dev` on `http://localhost:5173`) — `VITE_API_ORIGIN` is unset; the browser hits the SPA origin and the app-host / dev workflow serves API and SPA under the same origin.
  - **Azure Static Web Apps with a linked backend** (the shipped `azd up` topology) — `VITE_API_ORIGIN` is left unset so `/api/*` calls stay on the SWA origin and are proxied to the linked Container App backend. This is what the `azd-hooks/postprovision.*` `az staticwebapp backends link` step configures.
  - **Cross-origin SPA host** (e.g. a static bundle served from a different origin than the API) — set `VITE_API_ORIGIN=https://api.example.com` at build time; `resolveApiUrl` then rewrites REST calls to absolute URLs and CORS at the API takes over.
- **SignalR `/hubs/telemetry`** — the SPA-linked backend proxy on SWA does **not** proxy WebSockets, so `resolveTelemetryHubUrl(VITE_API_ORIGIN)` always prefers the absolute API origin when one is available and only falls back to the relative `/hubs/telemetry` path in dev. The Container App is public and validates the bearer token itself (`?access_token=<jwt>` for hubs), so the direct WebSocket handshake works without the SWA proxy.

A malformed `VITE_API_ORIGIN` (path, query, credentials, non-http protocol) is rejected — `resolveApiOrigin` returns `null` and callers stay on the same-origin default. This behavior is contract-tested by `apiOrigin.test.ts`, `telemetryHubUrl.test.ts`, and `authorizedFetch.test.ts`.

| Service | Endpoints | Used By |
|---------|-----------|---------|
| `api.ts` | `POST /api/chat` | ChatPanel |
| `approvalApi.ts` | `GET /api/approvals/pending`, `GET /api/approvals`, `POST /api/approvals/{id}/respond` | ApprovalCard, PendingApprovals, ApprovalHistory |
| `cardsApi.ts` | `GET /api/cards`, `POST /api/cards/{cardId}/vote`, `POST /api/cards/{cardId}/comments` | AdaptiveCardPanel, CardComments |
| `competitiveApi.ts` | `GET /api/competitive/pricing`, `GET /api/competitive/market-share`, `GET /api/competitive/threats`, `GET /api/competitive/competitor/{name}`, `POST /api/competitive/threats/{threatId}/response-plan` | CompetitiveDashboard, ThreatCards |
| `councilApi.ts` | `POST /api/council/convene`, `GET /api/council/history` | CouncilPanel, CouncilHistory |
| `guardrailsApi.ts` | `GET /api/guardrails/stats`, `GET /api/guardrails/config`, `PUT /api/guardrails/config`, `POST /api/guardrails/config/reset` | GuardrailsDashboard, GuardrailsConfig |
| `knowledgeApi.ts` | `GET /api/knowledge/documents`, `POST /api/knowledge/upload`, `DELETE /api/knowledge/documents/{id}`, `POST /api/knowledge/search`, `GET /api/knowledge/stats` | KnowledgeBasePanel, DocumentUpload, KnowledgeStats |
| `marginApi.ts` | `GET /api/margin/waterfall`, `GET /api/margin/drivers`, `GET /api/escalation/{traceId}` | MarginWaterfall, MarginDrivers, EscalationPath |
| `memoryApi.ts` | `GET /api/memory`, `DELETE /api/memory/{id}`, `DELETE /api/memory` | MemoryPanel |
| `observabilityApi.ts` | `GET /api/observability/costs`, `GET /api/observability/costs/agents`, `GET /api/observability/costs/trend`, `GET /api/observability/costs/tools`, `GET /api/observability/audit`, `GET /api/observability/export/sessions`, `GET /api/observability/export/{sessionId}/preview`, `POST /api/observability/export/{sessionId}` | CostDashboard, AuditLogViewer, ConversationExport |
| `promoApi.ts` | `POST /api/taskmodule/promo`, `GET /api/campaigns`, `POST /api/taskmodule/promo/submit` | PromoTaskModule |
| `scorecardApi.ts` | `GET /api/portfolio/scorecard`, `GET /api/portfolio/brand/{brandName}`, `GET /api/explain/{traceId}` | PortfolioScorecard, BrandScoreCard, ExplanationPanel |
| `storeApi.ts` | `GET /api/stores/performance`, `GET /api/stores/{storeId}/planogram`, `GET /api/stores/stockout-risks` | StoreHeatmap, PlanogramDiagram, StockoutAlert, StorePerformanceTable |
| `telemetryHub.ts` | SignalR `/hubs/telemetry` — `connectTelemetryHub()`, `joinTelemetrySession()`, `disconnectTelemetryHub()` | Dashboard, ChatPanel |

## Real-Time (SignalR)

The app maintains one SignalR connection to `/hubs/telemetry`. Two client methods are
invoked (`JoinSession`, plus the connection lifecycle handshake); the hub then dispatches
the following server-to-client events (verified against
`src/RetailPulse.Web/src/services/telemetryHub.ts`,
`src/RetailPulse.Web/src/components/Dashboard.tsx`, and
`src/RetailPulse.Web/src/components/cards/AdaptiveCardPanel.tsx`):

| Event | Direction | Handler | Used By |
|-------|-----------|---------|---------|
| `Connected` | Server → Client | Startup handshake — session banner + reconnect logging | telemetryHub |
| `SpanReceived` | Server → Client | Push a single agent/tool span onto the live-spans list | Dashboard (`liveSpans`) |
| `progress` | Server → Client | Per-turn progress ticks (phase, duration, tokens if available) | Dashboard (streaming progress) |
| `approval_requested` | Server → Client | New pending approval | Dashboard (`pendingApprovals`) |
| `approval_resolved` | Server → Client | Approval decided (`id`, `status`, `decidedBy`, `decidedAt`) | Dashboard (`approvalHistory`) |
| `alert_fired` | Server → Client | Guardrails / operational alert | Dashboard (`alerts`) |
| `trace_started` | Server → Client | New trace opened for a chat turn | Dashboard (`traces`) |
| `span_completed` | Server → Client | Span closed with duration/tokens | Dashboard (`traces`) |
| `trace_completed` | Server → Client | Trace closed — final totals | Dashboard (`traces`) |
| `card:action` | Server → Client | Adaptive Card action (vote, comment, sign-off) | AdaptiveCardPanel |
| `card:lifecycle` | Server → Client | Adaptive Card lifecycle change (created, resolved, expired) | AdaptiveCardPanel |

The client calls `connection.invoke('JoinSession', sessionId)` after every connect / reconnect so the hub can broadcast events to the correct SignalR group.

## Constants & Theming

All agent-related colors and config live in `constants/agentRouting.ts`:

- **`AGENT_ROUTING_CONFIG`** — per-intent color, emoji, label (demand=indigo, promo=green, supply=orange, competitive=red, sentiment=purple, general=gray)
- **`FORECAST_COLORS` / `SEASONAL_COLORS`** — chart theme colors for forecast views
- **`COUNCIL_COLORS` / `COUNCIL_DOMAIN_CONFIG`** — health indicator colors and domain icons
- **`CARD_COLORS` / `CARD_TYPE_CONFIG` / `CARD_LIFECYCLE_CONFIG`** — card state machine colors
- **`OBSERVABILITY_COLORS`** — cyan-themed observability accent
- **`STORE_COLORS` / `MARGIN_COLORS` / `SCORECARD_COLORS`** — domain-specific palettes

## Build & Test

```bash
cd src/RetailPulse.Web

# Development
npm run dev          # Vite dev server with HMR

# Build
npm run build        # tsc -b && vite build

# Test
npm run test         # vitest run (single pass)
npm run test:watch   # vitest (watch mode)

# Lint
npm run lint         # eslint
```
