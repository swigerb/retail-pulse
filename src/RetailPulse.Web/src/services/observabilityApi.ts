import type {
  CostDashboardData,
  ObservabilityPeriod,
  AuditLogPage,
  AuditLogFilters,
  ExportSession,
  ExportPreview,
} from '../types';

const BASE = '/api/observability';

export async function fetchCostDashboard(period: ObservabilityPeriod): Promise<CostDashboardData> {
  const res = await fetch(`${BASE}/costs?period=${period}`);
  if (!res.ok) throw new Error(`Cost data fetch failed: ${res.status}`);
  return res.json();
}

export async function fetchAuditLog(
  filters: AuditLogFilters,
  page: number = 1,
  pageSize: number = 50,
): Promise<AuditLogPage> {
  const params = new URLSearchParams();
  params.set('page', String(page));
  params.set('pageSize', String(pageSize));
  if (filters.agent) params.set('agent', filters.agent);
  if (filters.startDate) params.set('startDate', filters.startDate);
  if (filters.endDate) params.set('endDate', filters.endDate);
  if (filters.actionType) params.set('actionType', filters.actionType);
  if (filters.searchText) params.set('search', filters.searchText);
  const res = await fetch(`${BASE}/audit?${params.toString()}`);
  if (!res.ok) throw new Error(`Audit log fetch failed: ${res.status}`);
  return res.json();
}

export async function fetchExportSessions(): Promise<ExportSession[]> {
  const res = await fetch(`${BASE}/export/sessions`);
  if (!res.ok) throw new Error(`Sessions fetch failed: ${res.status}`);
  return res.json();
}

export async function fetchExportPreview(sessionId: string): Promise<ExportPreview> {
  const res = await fetch(`${BASE}/export/${sessionId}/preview`);
  if (!res.ok) throw new Error(`Preview fetch failed: ${res.status}`);
  return res.json();
}

export async function exportSession(sessionId: string, format: 'markdown' | 'json'): Promise<Blob> {
  const res = await fetch(`${BASE}/export/${sessionId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ format }),
  });
  if (!res.ok) throw new Error(`Export failed: ${res.status}`);
  return res.blob();
}
