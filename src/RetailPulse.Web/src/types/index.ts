export interface ChatRequest {
  message: string;
  sessionId?: string;
}

export interface ChatResponse {
  reply: string;
  sessionId: string;
  spans: AgentSpan[];
  charts?: ChartSpec[];
}

export interface ChartSpec {
  type: string;
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

export interface AgentSpan {
  name: string;
  type: 'thought' | 'tool_call' | 'tool_result' | 'response' | 'agent_delegation' | 'agent_call' | 'agent_response';
  detail: string;
  durationMs: number;
  timestamp: string;
}
