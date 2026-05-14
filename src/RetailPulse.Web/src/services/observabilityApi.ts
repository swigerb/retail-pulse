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
  const res = await fetch(`${BASE}/costs?period=${period}`, { signal });
  if (!res.ok) throw new Error(`Cost data fetch failed: ${res.status}`);
  const raw = await res.json();
  // Backend may return flat summary fields or nested structure
  const summary = raw.summary ?? {
    totalTokens: raw.totalTokens ?? 0,
    totalCost: raw.totalCost ?? 0,
    requestCount: raw.requestCount ?? 0,
    avgCostPerRequest: raw.avgCostPerRequest ?? (raw.requestCount ? raw.totalCost / raw.requestCount : 0),
  };
  return {
    summary,
    trend: raw.trend ?? [],
    agentBreakdown: raw.agentBreakdown ?? [],
    topTools: raw.topTools ?? [],
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
  return Array.isArray(data) ? data : [];
}

export async function fetchExportPreview(
  sessionId: string,
  signal?: AbortSignal,
): Promise<ExportPreview> {
  const res = await fetch(`${BASE}/export/${sessionId}/preview`, { signal });
  if (res.status === 404) return { sessionId, messages: [], totalMessages: 0 };
  if (!res.ok) throw new Error(`Preview fetch failed: ${res.status}`);
  const data = await res.json();
  return {
    sessionId: data?.sessionId ?? sessionId,
    messages: data?.messages ?? [],
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
