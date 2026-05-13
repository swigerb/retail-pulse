import type { CouncilConveneResponse, CouncilSession } from '../types';

export async function conveneCouncil(brand: string, region?: string): Promise<CouncilConveneResponse> {
  const res = await fetch('/api/council/convene', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ brand, region }),
  });
  if (!res.ok) throw new Error(`Council convene failed: ${res.status}`);
  return res.json();
}

export async function fetchCouncilHistory(limit = 20): Promise<CouncilSession[]> {
  const res = await fetch(`/api/council/history?limit=${limit}`);
  if (!res.ok) throw new Error(`Failed to fetch council history: ${res.status}`);
  return res.json();
}
