export interface ChatHistoryMessage {
  role: 'user' | 'assistant';
  content: string;
}

/**
 * Hybrid execution decision (issue #95). Mirrors the stable UI + telemetry
 * contract values in `RetailPulse.Contracts.Routing.ExecutionPath`:
 *   - `fast`    — single-specialist single-shot path (today's default)
 *   - `plan`    — plan-first workflow path (issue #93)
 *   - `council` — dedicated portfolio-health council interception
 */
export type ExecutionPath = 'fast' | 'plan' | 'council';

/**
 * Paths the UI is allowed to force via the composer. Council is a
 * router-controlled destination with its own dedicated trigger and is never
 * a user override — the backend validator rejects it with a 400.
 */
export type ForceableExecutionPath = 'fast' | 'plan';

export interface ChatRequest {
  message: string;
  sessionId?: string;
  history?: ChatHistoryMessage[];
  /**
   * Optional user override for the hybrid execution decider (issue #95).
   * Omitted for the "Auto" composer default so the backend chooses. Only
   * `fast` or `plan` are accepted; anything else 400s server-side.
   */
  forceExecutionPath?: ForceableExecutionPath;
}

export type IntentCategory =
  | 'demand'
  | 'promotion'
  | 'supply'
  | 'competitive'
  | 'sentiment'
  | 'general';

export interface RoutingInfo {
  agentKey: string;
  agentName: string;
  intent: string;
  confidence: number;
  durationMs?: number;
  /**
   * Execution path the backend chose for this reply (issue #95). Optional
   * to stay backward-compatible with pre-#95 responses that never carry it.
   */
  executionPath?: ExecutionPath;
  /**
   * True when the chosen path came from an explicit `forceExecutionPath`
   * override rather than the router's automatic decision. Optional for
   * pre-#95 payload compatibility.
   */
  executionPathForced?: boolean;
}

/** Extract the top-level intent category from a slash-separated intent string.
 *  e.g. "demand/forecasting" → "demand", "council/health" → "general" */
export function getIntentCategory(intent: string | undefined): IntentCategory {
  if (!intent) return 'general';
  const prefix = intent.split('/')[0].toLowerCase();
  const valid: IntentCategory[] = ['demand', 'promotion', 'supply', 'competitive', 'sentiment'];
  return (valid as string[]).includes(prefix) ? (prefix as IntentCategory) : 'general';
}

export interface ChatResponse {
  reply: string;
  sessionId: string;
  spans: AgentSpan[];
  charts?: ChartSpec[];
  routing?: RoutingInfo;
  totalDurationMs?: number;
  tokenUsage?: TokenUsage;
  memoryContext?: MemoryContext;
  /**
   * Populated when the router picked the plan-first path (#93). Lets the UI
   * open the plan surface for the returned reply without a separate lookup.
   * Always omitted on fast-path replies.
   */
  planId?: string;
}

export interface TokenUsage {
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  estimatedCostUsd?: number;
}

export type ChartType =
  | 'line'
  | 'bar'
  | 'groupedBar'
  | 'stackedBar'
  | 'horizontalBar'
  | 'pie'
  | 'donut'
  | 'gauge'
  | 'table';

export interface ChartSpec {
  type: ChartType;
  title: string;
  xAxisTitle?: string;
  yAxisTitle?: string;
  data: ChartSeries[];
}

export interface ChartSeries {
  legend: string;
  color?: string;
  values: ChartDataPoint[];
}

export interface ChartDataPoint {
  x: string;
  y: number;
}

// Demand Forecast data shape returned by the Demand Forecasting Agent
export interface ForecastData {
  brand: string;
  region: string;
  period: { start: string; end: string };
  historical: Array<{ date: string; actual: number }>;
  predicted: Array<{ date: string; value: number; lower: number; upper: number }>;
  seasonality: Array<{ factor: string; impact: string; period: string; startDate: string; endDate: string }>;
  risks: Array<{ type: string; severity: 'low' | 'medium' | 'high'; description: string; affectedPeriod: string }>;
}

export interface AgentSpan {
  name: string;
  type: 'thought' | 'tool_call' | 'tool_result' | 'response' | 'agent_delegation' | 'agent_call' | 'agent_response' | 'routing';
  detail: string;
  durationMs: number;
  timestamp: string;
  inputTokens?: number;
  outputTokens?: number;
}

// --- Memory types (Sprint 1.3) ---

export type MemoryType = 'conversation' | 'preference' | 'entity';

export interface MemoryEntry {
  id: string;
  type: MemoryType;
  content: string;
  storedAt: string;
  expiresAt?: string;
  tags?: string[];
}

export interface MemoryContext {
  entries: MemoryEntry[];
  summary: string;
}

// --- Approval types (Sprint 1.4) ---

export type ApprovalUrgency = 'high' | 'medium' | 'low';
export type ApprovalDecision = 'approved' | 'rejected' | 'modified' | 'timed_out' | 'pending';

export interface ApprovalRequest {
  id: string;
  action: string;
  reasoning: string;
  impact: string;
  urgency: ApprovalUrgency;
  agentId: string;
  agentName: string;
  requestedAt: string;
  timeoutAt: string;
  status: ApprovalDecision;
  decidedBy?: string;
  decidedAt?: string;
  comment?: string;
}

export interface ApprovalResponse {
  decision: 'approved' | 'rejected' | 'modified';
  comment?: string;
}

// --- Alert types (Sprint 1.5) ---

export type AlertSeverity = 'high' | 'medium' | 'low';
export type AlertStatus = 'active' | 'snoozed' | 'dismissed';
export type SnoozeDuration = '1h' | '4h' | '24h' | '1wk';

export interface Alert {
  id: string;
  title: string;
  severity: AlertSeverity;
  brand?: string;
  region?: string;
  changePercent?: number;
  description: string;
  recommendedAction: string;
  firedAt: string;
  status: AlertStatus;
  snoozedUntil?: string;
}

// --- Trace types (Sprint 1.6) ---

export type TraceSpanType = 'routing' | 'agent' | 'tool' | 'memory' | 'approval';

export interface TraceSpan {
  id: string;
  parentId?: string;
  name: string;
  type: TraceSpanType;
  startTime: string;
  durationMs: number;
  attributes?: Record<string, string>;
  inputTokens?: number;
  outputTokens?: number;
  estimatedCostUsd?: number;
}

export interface Trace {
  traceId: string;
  intent: string;
  agentName: string;
  model?: string;
  startTime: string;
  totalDurationMs: number;
  totalTokens: number;
  totalCostUsd: number;
  spans: TraceSpan[];
  status: 'in_progress' | 'completed' | 'error';
}

// --- Promotion Planning types (Sprint 2.1) ---

export type PromoType = 'Discount' | 'BOGO' | 'Display' | 'Digital' | 'Bundle';
export type PromoRecommendationLevel = 'recommended' | 'cautious' | 'not_recommended';

export interface PromoRisk {
  type: string;
  detail: string;
  severity: 'low' | 'medium' | 'high';
}

export interface PromoEvaluation {
  recommendation: PromoRecommendationLevel;
  roi: number;
  roiLower: number;
  roiUpper: number;
  reasoning: string;
  timingAssessment: string;
  conflicts: string[];
  seasonalityFit: string;
  risks: PromoRisk[];
  similarCampaigns: number;
  breakEvenDays: number;
  historicalAvgRoi: number;
}

export interface PromoCampaign {
  id: string;
  name: string;
  brand: string;
  region: string;
  promoType: PromoType;
  budget: number;
  startDate: string;
  endDate: string;
  roi?: number;
  status: 'active' | 'completed' | 'planned' | 'proposed';
}

export interface PromoFormData {
  brand: string;
  region: string;
  promoType: PromoType;
  budget: number;
  startDate: string;
  endDate: string;
  targetLiftPercent?: number;
}

// --- Competitive Intelligence types (Sprint 2.2) ---

export type ThreatSeverity = 'high' | 'medium' | 'low';
export type ThreatRecommendation = 'MATCH' | 'DIFFERENTIATE' | 'IGNORE' | 'PREEMPT';

export interface CompetitorPricing {
  competitor: string;
  sku: string;
  category: string;
  currentPrice: number;
  previousPrice: number;
  changePercent: number;
  priceHistory: Array<{ month: string; price: number }>;
}

export interface MarketShareEntry {
  quarter: string;
  brand: string;
  share: number;
  isOurBrand: boolean;
}

export interface CompetitiveEvent {
  date: string;
  description: string;
  competitor: string;
}

export interface CompetitiveThreat {
  id: string;
  title: string;
  severity: ThreatSeverity;
  recommendation: ThreatRecommendation;
  description: string;
  reasoning: string;
  historicalContext: string;
  competitor: string;
  category: string;
  detectedAt: string;
}

export interface CompetitorOverview {
  name: string;
  categories: string[];
  regions: string[];
  recentMoves: Array<{ date: string; action: string }>;
  pricingHistory: Array<{ month: string; avgPrice: number }>;
  marketShare: number;
}

// --- Council / Portfolio Health types (Sprint 2.4) ---

export type HealthRating = 'green' | 'yellow' | 'red';

export interface CouncilAgentVote {
  agentId: string;
  agentName: string;
  domain: 'demand' | 'supply' | 'competitive';
  rating: HealthRating;
  confidence: number;
  reasoning: string;
  keyMetrics: string[];
  responseTimeMs: number;
  timedOut?: boolean;
}

export interface CouncilDisagreement {
  topic: string;
  agents: Array<{ agentName: string; position: string }>;
  resolution: string;
  dominantAgent: string;
  dominantReason: string;
}

export interface CouncilVerdict {
  overallRating: HealthRating;
  unanimous: boolean;
  synthesisText: string;
  disagreements: CouncilDisagreement[];
  actionItems: Array<{ priority: number; text: string }>;
  totalConveneTimeMs: number;
}

export interface CouncilSession {
  id: string;
  brand: string;
  region?: string;
  convenedAt: string;
  votes: CouncilAgentVote[];
  verdict: CouncilVerdict;
}

export interface CouncilConveneRequest {
  brand: string;
  region?: string;
}

export interface CouncilConveneResponse {
  sessionId: string;
  brand: string;
  region?: string;
  votes: CouncilAgentVote[];
  verdict: CouncilVerdict;
}

// --- Knowledge Base types (Sprint 2.3) ---
// Aligned with backend DTOs: DocumentInfo, SearchResult, /api/knowledge/stats response

export interface KBDocument {
  id: string;
  title: string;
  source: string;
  ingestedAt: string;
  chunkCount: number;
}

export interface KBSearchResult {
  documentId: string;
  title: string;
  chunk: string;
  score: number;
  source: string;
  chunkIndex: number;
}

export interface KBStats {
  documentCount: number;
  chunkCount: number;
  averageChunksPerDocument: number;
}

export interface KnowledgeUploadResponse {
  documentId: string;
  title: string;
  status: string;
  chunkCount?: number;
  source?: string;
}

export interface Citation {
  sourceName: string;
  sourceTitle: string;
  chunkPreview: string;
  relevanceScore: number;
}

// --- Knowledge provider snapshot (issue #106) ---
// Aligned with GET /api/knowledge/provider, backed by the KnowledgeBaseCapabilities
// record on the backend. Scores are provider-local — the frontend never compares
// them across providers, and `scoreSemantics` MUST be surfaced verbatim so the
// user reads honest, provider-specific relevance meaning.

export type KnowledgeRelevanceKind = 'Lexical' | 'Semantic' | 'Hybrid';

export type KnowledgeDegradationMode = 'FailLoud' | 'FallbackToInMemory';

export interface KnowledgeProviderInfo {
  name: string;
  relevance: KnowledgeRelevanceKind;
  persistent: boolean;
  requiresCloud: boolean;
  supportsMutation: boolean;
  scoreSemantics: string;
}

export interface KnowledgeDegradationInfo {
  mode: KnowledgeDegradationMode | null;
  primaryReplacedByFallback: boolean;
}

export interface KnowledgeQuotas {
  maxDocuments: number;
  maxChunks: number;
  maxDocumentSizeBytes: number;
}

export interface KnowledgeUsage {
  documentCount: number;
  chunkCount: number;
}

export interface KnowledgeNamedSource {
  name: string;
  documents: string[];
}

export interface KnowledgeAgentBinding {
  agentKey: string;
  agentDisplayName: string;
  enabled: boolean;
  sourceName: string;
  sources: string[];
}

export interface KnowledgeProviderSnapshot {
  provider: KnowledgeProviderInfo;
  degradation: KnowledgeDegradationInfo;
  quotas: KnowledgeQuotas;
  usage: KnowledgeUsage;
  sources: KnowledgeNamedSource[];
  bindings: KnowledgeAgentBinding[];
}

// --- Streaming types (Sprint 3.1) ---

export interface StreamingToken {
  content: string;
  index: number;
}

export interface CacheInfo {
  cached: boolean;
  ttlSeconds?: number;
  timeSavedMs?: number;
}

// --- Guardrails types (Sprint 3.2) ---

export type GuardrailDetectionType =
  | 'jailbreak'
  | 'pii'
  | 'access'
  | 'content-safety-hate'
  | 'content-safety-sexual'
  | 'content-safety-violence'
  | 'content-safety-selfharm'
  | 'content-safety-prompt-shield'
  | 'content-safety-indirect-injection'
  | 'content-safety-unavailable';

export interface BlockedRequest {
  id: string;
  timestamp: string;
  requestPreview: string;
  detectionType: GuardrailDetectionType;
  reason: string;
  actionTaken: string;
  /**
   * Content-safety category (Hate/Sexual/Violence/SelfHarm) when the block
   * originated from the model-based Content Safety layer. Populated by the
   * backend `SuspiciousRequest.Category` field — see
   * `RetailPulse.Contracts.Guardrails.SuspiciousRequest`.
   */
  category?: string;
  /**
   * Severity on the Content Safety 0/2/4/6 axis when a category hit is present.
   * Never a raw threshold value — this is the hit severity only.
   */
  severity?: number;
  /**
   * Decision label — one of `Blocked`, `Flagged`, `ServiceUnavailable`. Used by
   * the dashboard to split the pattern-family and model-family aggregates.
   */
  decision?: string;
}

export interface GuardrailsStats {
  totalBlocked: number;
  jailbreakAttempts: number;
  piiDetections: number;
  accessDenials: number;
  contentSafetyBlocks: number;
  contentSafetyFlags: number;
  recentBlocked: BlockedRequest[];
  blocksPerHour: Array<{ hour: string; count: number }>;
}

export interface GuardrailsConfigData {
  jailbreakEnabled: boolean;
  piiEnabled: boolean;
  accessControlEnabled: boolean;
  blockedPatterns: string;
  contentSafety?: ContentSafetyConfigData;
}

export type ContentSafetyFailPolicy = 'FailOpen' | 'FailClosed';

// Aligned with `ContentSafetyConfigResponse` in
// src/RetailPulse.Api/Endpoints/GuardrailEndpoints.cs — thresholds are
// serialised as flat fields (`hateThreshold`, `sexualThreshold`, etc.), not a
// nested object.
export interface ContentSafetyConfigData {
  enabled: boolean;
  failPolicy: ContentSafetyFailPolicy;
  promptShieldsEnabled: boolean;
  checkInput: boolean;
  checkOutput: boolean;
  checkRetrievedKnowledge: boolean;
  checkToolResults: boolean;
  hateThreshold: number;
  sexualThreshold: number;
  violenceThreshold: number;
  selfHarmThreshold: number;
}

export type PiiRedactionType = 'email' | 'phone' | 'ssn' | 'address' | 'name' | 'credit_card' | 'unknown';

// --- Safety block display types (issue #101) -----------------------------
//
// The frontend deliberately whitelists the fields it renders when it explains
// a safety decision to the user. Internal rule details (regex patterns,
// threshold values, analyzer names, rule IDs) are NEVER carried in a
// `SafetyBlockDisplayModel` so the display layer cannot leak them by mistake.

/** Well-known content-safety category names emitted by the backend. */
export type SafetyCategoryName = 'Hate' | 'Sexual' | 'Violence' | 'SelfHarm';

/**
 * Terminal Content Safety decision label. Mirrors
 * `RetailPulse.Api.Guardrails.ContentSafety.ContentSafetyDecision`.
 */
export type SafetyDecisionKind = 'Blocked' | 'Flagged' | 'ServiceUnavailable' | 'Passed';

/**
 * Pipeline stage where the safety block occurred. Matches the backend
 * `ContentSafetyStage` enum plus the frontend-only `plan-step` and
 * `ingestion` stages that describe how the block is surfaced to the user.
 */
export type SafetyBlockStage =
  | 'input'
  | 'output'
  | 'plan-step'
  | 'ingestion'
  | 'retrieved-knowledge'
  | 'tool-result';

/**
 * Family a safety block belongs to. `pattern` = local regex/substring
 * guardrails; `model` = Content Safety / Prompt Shields; `unknown` = a
 * detection type the frontend does not classify yet (rendered generically).
 */
export type SafetyBlockFamily = 'pattern' | 'model' | 'unknown';

/**
 * Whitelisted display model used by every safety-related component. The
 * fields on this shape are the ONLY things the UI is allowed to render for a
 * safety decision — no raw pattern, threshold, rule ID, or provider payload
 * may ever be added here.
 */
export interface SafetyBlockDisplayModel {
  stage: SafetyBlockStage;
  family: SafetyBlockFamily;
  /** Plain-language reason shown as the primary line. */
  reason: string;
  /** Optional plain-language rephrasing suggestion (never a rule detail). */
  suggestion?: string;
  /** Plain-language category label, e.g. "Hateful content". */
  categoryLabel?: string;
  /** Original category name for testids / analytics — never a rule name. */
  categoryName?: SafetyCategoryName;
  /** Plain-language severity descriptor, e.g. "high". */
  severityLabel?: 'low' | 'medium' | 'high' | 'severe';
  /** Original decision label — Blocked / Flagged / ServiceUnavailable. */
  decision?: SafetyDecisionKind;
  /**
   * When true, the block came from the model-based Content Safety layer.
   * Used by the dashboard to split pattern vs model aggregates.
   */
  modelBased: boolean;
  /** Deployment fail policy hint for service-unavailable renders. */
  failClosed?: boolean;
}

// --- Collaborative Adaptive Cards types (Sprint 3.3) ---

export type CardType = 'voting' | 'drilldown' | 'dashboard' | 'briefing';
export type CardLifecycleState = 'active' | 'voting' | 'decided' | 'archived';
export type VoteChoice = 'approve' | 'reject' | 'abstain';

export interface CardComment {
  id: string;
  userId: string;
  userName: string;
  text: string;
  timestamp: string;
}

export interface UserVote {
  userId: string;
  userName: string;
  choice: VoteChoice;
  votedAt: string;
}

export interface AdaptiveCard {
  id: string;
  type: CardType;
  title: string;
  summary: string;
  state: CardLifecycleState;
  stateChangedAt: string;
  createdAt: string;
  createdBy: string;
  votes?: UserVote[];
  comments?: CardComment[];
  data?: Record<string, unknown>;
  escalated?: boolean;
  escalationReason?: string;
}

export interface DrillDownLevel {
  label: string;
  data: Array<{ name: string; value: number; subItems?: Array<{ name: string; value: number }> }>;
}

// --- Observability types (Sprint 3.4) ---

export type ObservabilityPeriod = 'today' | 'week' | 'month';

export interface CostSummary {
  totalTokens: number;
  totalCost: number;
  requestCount: number;
  avgCostPerRequest: number;
}

export interface CostTrendPoint {
  date: string;
  cost: number;
  tokens: number;
  requests?: number;
}

export interface AgentCostBreakdown {
  agentName: string;
  totalTokens: number;
  totalCost: number;
  requestCount: number;
}

export interface ToolUsageEntry {
  toolName: string;
  callCount: number;
  /**
   * Wall-clock time spent in this tool across all calls. Deliberately not tokens: tool
   * spans are MCP round trips and never carry model tokens, so a per-tool token figure
   * could only ever be zero.
   */
  totalDurationMs: number;
  avgDurationMs: number;
}

export interface CostDashboardData {
  summary: CostSummary;
  trend: CostTrendPoint[];
  agentBreakdown: AgentCostBreakdown[];
  topTools: ToolUsageEntry[];
}

export interface AuditLogEntry {
  id: string;
  timestamp: string;
  userId: string;
  agentId: string;
  action: string;
  inputSummary: string;
  outputSummary: string;
  tokens: number;
  durationMs: number;
}

export interface AuditLogFilters {
  agent?: string;
  startDate?: string;
  endDate?: string;
  actionType?: string;
  searchText?: string;
}

export interface AuditLogPage {
  entries: AuditLogEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ExportSession {
  sessionId: string;
  startTime: string;
  messageCount: number;
  agentsUsed: string[];
  totalTokens: number;
}

export interface ExportPreview {
  sessionId: string;
  messages: Array<{ role: 'user' | 'assistant'; content: string; timestamp: string }>;
  totalMessages: number;
}

// --- Store Operations types (Sprint 4.1) ---

export type PerformanceLevel = 'green' | 'yellow' | 'red';

export interface StorePerformance {
  storeId: string;
  storeName: string;
  region: string;
  revenue: number;
  target: number;
  performanceIndex: number;
  issues: string[];
  recommendations: string[];
}

export interface PlanogramSlot {
  shelfLevel: number;
  position: number;
  skuName: string;
  brand: string;
  brandColor: string;
  facingWidth: number;
  predictedUplift?: number;
}

export interface PlanogramLayout {
  shelfCount: number;
  slots: PlanogramSlot[];
  eyeLevelShelves: number[];
}

export interface StockoutRisk {
  skuId: string;
  skuName: string;
  brand: string;
  currentVelocity: number;
  daysRemaining: number;
  recommendedReorder: number;
  region: string;
}

// --- Margin & Escalation types (Sprint 4.2) ---

export interface MarginWaterfallStep {
  label: string;
  value: number;
  isSubtotal?: boolean;
}

export interface MarginDriver {
  name: string;
  impact: number;
  trend: 'improving' | 'worsening' | 'stable';
  isRisk: boolean;
}

export type EscalationLevel = 'L1' | 'L2' | 'L3';

export interface EscalationStep {
  level: EscalationLevel;
  agentName: string;
  contextAdded: string;
  timeSpentMs: number;
  timestamp: string;
  isCurrent: boolean;
}

// --- Portfolio Scorecard & Explainability types (Sprint 4.3) ---

export interface BrandScore {
  brandName: string;
  healthScore: number;
  trend: 'up' | 'down' | 'stable';
  dimensions: {
    demand: number;
    margin: number;
    competitive: number;
    supply: number;
  };
  topRisk: string;
  topOpportunity: string;
}

export interface ExplanationStep {
  toolName: string;
  inputSummary: string;
  outputSummary: string;
  reasoning: string;
}

export interface ExplanationData {
  traceId: string;
  question: string;
  answer: string;
  steps: ExplanationStep[];
  confidence: number;
  dataSources: Array<{ name: string; url?: string }>;
  generatedAt: string;
}

// --- Plan types (issue #96) --------------------------------------------------
// Mirrors backend contracts in RetailPulse.Contracts.Persistence.PlanDtos and
// RetailPulse.Contracts.Approval.PlanReview. Every terminal/lifecycle string is
// kept as a discriminated union so the reducer, UI, and tests share one source
// of truth. New backend states are additive — the union widens rather than
// silently accepting `string`.

export type PlanStatus =
  | 'draft'
  | 'awaiting_review'
  | 'awaiting_clarification'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'unusable';

export type PlanStepStatus =
  | 'pending'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'timed_out'
  | 'skipped'
  | 'unusable';

/** One persisted plan step. Mirrors backend `PlanStepRecordDto`. */
export interface PlanStep {
  stepId: string;
  planId: string;
  stepIndex: number;
  specialistKey: string;
  intent: string;
  action: string;
  status: PlanStepStatus;
  result?: string | null;
  error?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  totalTokens?: number | null;
  durationMs?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  /** Per-step charts persisted by the executor (issue #96). Optional; older plans have none. */
  charts?: ChartSpec[] | null;
}

/** Full plan detail with ordered steps. Mirrors backend `PlanDetailDto`. */
export interface PlanDetail {
  planId: string;
  sessionId?: string | null;
  tenantId?: string | null;
  request: string;
  status: PlanStatus;
  detectedIntents: string[];
  failureReason?: string | null;
  totalInputTokens?: number | null;
  totalOutputTokens?: number | null;
  totalTokens?: number | null;
  totalDurationMs?: number | null;
  createdAt: string;
  updatedAt: string;
  steps: PlanStep[];
}

/** List-item form of a plan. Mirrors backend `PlanSummaryDto`. */
export interface PlanSummary {
  planId: string;
  sessionId?: string | null;
  tenantId?: string | null;
  request: string;
  status: PlanStatus;
  stepCount: number;
  createdAt: string;
  updatedAt: string;
}

/** Step shape carried in a plan review proposal. */
export interface PlanReviewStep {
  specialistKey: string;
  intent: string;
  action: string;
}

/** Discriminator for a plan review decision. */
export type PlanReviewDecisionKind = 'approve' | 'reject' | 'edit';

/** Body sent to POST /api/plans/{planId}/reviews/{requestId}/decision. */
export interface PlanReviewDecisionRequest {
  kind: PlanReviewDecisionKind;
  comment?: string;
  feedback?: string;
  editedSteps?: PlanReviewStep[];
}

/** Response envelope from POST plan-review decision. */
export interface PlanReviewDecisionResponse {
  requestId: string;
  planId: string;
  decision: string;
  kind: PlanReviewDecisionKind;
  comment?: string | null;
  respondedAt: string;
  terminalReason?: string | null;
  round: number;
}

/** Shape returned by GET /api/plans/{planId}/reviews — one open review. */
export interface PlanReviewOpen {
  requestId: string;
  planId: string;
  round: number;
  subject?: string;
  action?: string;
  impact?: string;
  urgency?: string;
  reasoning?: string;
  createdAt: string;
  expiresAt?: string;
  status?: string;
  /** Serialized `PlanReviewProposal` JSON returned by the backend. */
  payload?: string | null;
}

/** Deserialized review proposal (from `PlanReviewOpen.payload`). */
export interface PlanReviewProposal {
  planId: string;
  roundNumber: number;
  request: string;
  steps: PlanReviewStep[];
  revisionReason?: string | null;
}

/** Deserialized clarification prompt (from an approval payload). */
export interface PlanClarificationPrompt {
  planId: string;
  stepIndex: number;
  specialistKey: string;
  question: string;
}

/** Payload broadcast when the reviewer's decision resolves. */
export interface PlanReviewResolvedEvent {
  requestId: string;
  planId: string;
  decision: string;
  kind: PlanReviewDecisionKind;
  comment?: string | null;
  respondedAt: string;
  terminalReason?: string | null;
  round: number;
}

/** Payload broadcast when a rejected plan is replanned for another round. */
export interface PlanReviewNextRoundEvent {
  planId: string;
  requestId: string;
  round: number;
}

/** Payload broadcast when the plan reaches a terminal reply. */
export interface PlanFinalResponseEvent {
  planId: string;
  subject?: string;
  reply: string;
  terminalReason?: string | null;
  /**
   * Optional aggregate charts produced by specialists during plan execution
   * (issue #141). Populated on the review-resume path by
   * `PlanReviewCompletionService.BroadcastFinalAsync`. The immediate path
   * carries the same shape inline on `ChatResponse.charts`, so both plan
   * paths converge on `ChartSpec[]` for rendering.
   */
  charts?: ChartSpec[] | null;
}

/** 202 Accepted body returned by POST /api/chat when a plan suspends for review. */
export interface PlanSuspendedResponse {
  planId: string;
  status: PlanStatus;
  reviewRequestId?: string | null;
  round?: number | null;
  sessionId: string;
  message?: string;
}

/** Union returned by the enhanced `sendMessage` service. */
export type SendMessageResult =
  | { kind: 'complete'; response: ChatResponse }
  | { kind: 'suspended'; suspended: PlanSuspendedResponse };
