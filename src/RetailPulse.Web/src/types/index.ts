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
  agentId: string;
  agentName: string;
  intentCategory: IntentCategory;
  confidence: number;
  reasoning?: string;
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
