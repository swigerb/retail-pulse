const API_BASE = '/api';

export async function fetchMarginWaterfall(period?: string): Promise<import('../types').MarginWaterfallStep[]> {
  const params = period ? `?period=${encodeURIComponent(period)}` : '';
  const res = await fetch(`${API_BASE}/margin/waterfall${params}`);
  if (!res.ok) throw new Error(`Failed to fetch margin waterfall: ${res.status}`);
  return res.json();
}

export async function fetchMarginDrivers(): Promise<import('../types').MarginDriver[]> {
  const res = await fetch(`${API_BASE}/margin/drivers`);
  if (!res.ok) throw new Error(`Failed to fetch margin drivers: ${res.status}`);
  return res.json();
}

export async function fetchEscalationPath(traceId: string): Promise<import('../types').EscalationStep[]> {
  const res = await fetch(`${API_BASE}/escalation/${traceId}`);
  if (!res.ok) throw new Error(`Failed to fetch escalation path: ${res.status}`);
  return res.json();
}
