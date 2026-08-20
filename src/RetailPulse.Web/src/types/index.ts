export interface ChatHistoryMessage {
  role: 'user' | 'assistant';
  content: string;
}

export interface ChatRequest {
  message: string;
  sessionId?: string;
  history?: ChatHistoryMessage[];
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
}

export interface Citation {
  sourceName: string;
  sourceTitle: string;
  chunkPreview: string;
  relevanceScore: number;
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

export interface ContentSafetyCategoryThresholdsData {
  hate: number;
  sexual: number;
  violence: number;
  selfHarm: number;
}

export interface ContentSafetyConfigData {
  enabled: boolean;
  failPolicy: ContentSafetyFailPolicy;
  promptShieldsEnabled: boolean;
  checkInput: boolean;
  checkOutput: boolean;
  checkRetrievedKnowledge: boolean;
  checkToolResults: boolean;
  thresholds: ContentSafetyCategoryThresholdsData;
}

export type PiiRedactionType = 'email' | 'phone' | 'ssn' | 'address' | 'name' | 'credit_card' | 'unknown';

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
  totalTokens: number;
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
