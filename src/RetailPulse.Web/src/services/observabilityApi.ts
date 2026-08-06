import type {
  CostDashboardData,
  ObservabilityPeriod,
  AuditLogPage,
  AuditLogFilters,
  ExportSession,
  ExportPreview,
} from '../types';

const BASE = '/api/observability';

export async function fetchCostDashboard(
  period: ObservabilityPeriod,
  signal?: AbortSignal,
): Promise<CostDashboardData> {
  const daysByPeriod: Record<ObservabilityPeriod, number> = {
    today: 1,
    week: 7,
    month: 30,
  };

  const safeFetchJson = async (url: string, required = false): Promise<unknown> => {
    try {
      const res = await fetch(url, { signal });
      if (res.status === 404) return null;
      if (!res.ok) {
        if (required) throw new Error(`Cost data fetch failed: ${res.status}`);
        return null;
      }
      return res.json();
    } catch (e) {
      if (e instanceof Error && e.name === 'AbortError') throw e;
      if (required) throw e;
      return null;
    }
  };

  const [summaryRaw, agentsRaw, trendRaw, toolsRaw] = await Promise.all([
    safeFetchJson(`${BASE}/costs?period=${period}`, true),
    safeFetchJson(`${BASE}/costs/agents?period=${period}`),
    safeFetchJson(`${BASE}/costs/trend?days=${daysByPeriod[period]}`),
    safeFetchJson(`${BASE}/costs/tools?period=${period}`),
  ]);

  const raw = (summaryRaw ?? {}) as Partial<CostDashboardData['summary']> & { summary?: Partial<CostDashboardData['summary']> };
  const summarySource = raw.summary ?? raw;
  const requestCount = summarySource.requestCount ?? 0;
  const totalCost = summarySource.totalCost ?? 0;
  const summary = {
    totalTokens: summarySource.totalTokens ?? 0,
    totalCost,
    requestCount,
    avgCostPerRequest: summarySource.avgCostPerRequest ?? (requestCount ? totalCost / requestCount : 0),
  };

  const agentBreakdown = Array.isArray(agentsRaw)
    ? agentsRaw.map(agent => ({
      agentName: agent?.agentId ?? '',
      totalCost: agent?.cost ?? 0,
      totalTokens: agent?.tokens ?? 0,
      requestCount: agent?.requests ?? 0,
    }))
    : [];

  const trendDays = (trendRaw as { days?: Array<{ date?: string; cost?: number; tokens?: number }> } | null)?.days;
  const trend = Array.isArray(trendDays)
    ? trendDays.map(day => ({
      date: day?.date
        ? new Date(day.date).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
        : '',
      cost: day?.cost ?? 0,
      tokens: day?.tokens ?? 0,
    }))
    : [];

  const topTools = Array.isArray(toolsRaw)
    ? toolsRaw.map(tool => ({
      toolName: tool?.toolName ?? '',
      callCount: tool?.callCount ?? 0,
      totalTokens: tool?.totalTokens ?? 0,
      avgDurationMs: tool?.avgDurationMs ?? 0,
    }))
    : [];

  return {
    summary,
    trend,
    agentBreakdown,
    topTools,
  };
}

export async function fetchAuditLog(
  filters: AuditLogFilters,
  page: number = 1,
  pageSize: number = 50,
  signal?: AbortSignal,
): Promise<AuditLogPage> {
  const params = new URLSearchParams();
  params.set('page', String(page));
  params.set('pageSize', String(pageSize));
  params.set('limit', String(pageSize));
  if (filters.agent) params.set('agent', filters.agent);
  if (filters.agent) params.set('agentId', filters.agent);
  if (filters.startDate) params.set('startDate', filters.startDate);
  if (filters.startDate) params.set('from', filters.startDate);
  if (filters.endDate) params.set('endDate', filters.endDate);
  if (filters.endDate) params.set('to', filters.endDate);
  if (filters.actionType) params.set('actionType', filters.actionType);
  if (filters.actionType) params.set('action', filters.actionType);
  if (filters.searchText) params.set('search', filters.searchText);
  const res = await fetch(`${BASE}/audit?${params.toString()}`, { signal });
  if (res.status === 404) return { entries: [], totalCount: 0, page, pageSize };
  if (!res.ok) throw new Error(`Audit log fetch failed: ${res.status}`);
  const raw = await res.json();
  // Backend may return a flat array or the expected { entries, totalCount } shape
  if (Array.isArray(raw)) {
    return { entries: raw ?? [], totalCount: raw.length, page, pageSize };
  }
  return {
    entries: raw?.entries ?? [],
    totalCount: raw?.totalCount ?? (raw?.entries?.length ?? 0),
    page: raw?.page ?? page,
    pageSize: raw?.pageSize ?? pageSize,
  };
}

export async function fetchExportSessions(signal?: AbortSignal): Promise<ExportSession[]> {
  const res = await fetch(`${BASE}/export/sessions`, { signal });
  if (res.status === 404) return [];
  if (!res.ok) throw new Error(`Sessions fetch failed: ${res.status}`);
  const data = await res.json();
  if (!Array.isArray(data)) return [];
  return data
    .filter((session): session is Partial<ExportSession> => typeof session === 'object' && session !== null)
    .map(session => ({
      sessionId: session.sessionId ?? '',
      startTime: session.startTime ?? '',
      messageCount: session.messageCount ?? 0,
      agentsUsed: Array.isArray(session.agentsUsed) ? session.agentsUsed : [],
      totalTokens: session.totalTokens ?? 0,
    }));
}

export async function fetchExportPreview(
  sessionId: string,
  signal?: AbortSignal,
): Promise<ExportPreview> {
  const res = await fetch(`${BASE}/export/${sessionId}/preview`, { signal });
  if (res.status === 404) {
    // No silent success fallback — a missing session is a real error the UI surfaces.
    throw new Error(`Session '${sessionId}' not found`);
  }
  if (!res.ok) throw new Error(`Preview fetch failed: ${res.status}`);
  const data = await res.json();
  return {
    sessionId: data?.sessionId ?? sessionId,
    messages: Array.isArray(data?.messages) ? data.messages : [],
    totalMessages: data?.totalMessages ?? (data?.messages?.length ?? 0),
  };
}

export async function exportSession(
  sessionId: string,
  format: 'markdown' | 'json',
  signal?: AbortSignal,
): Promise<Blob> {
  const res = await fetch(`${BASE}/export/${sessionId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ format }),
    signal,
  });
  if (!res.ok) throw new Error(`Export failed: ${res.status}`);
  return res.blob();
}
