const API_BASE = '/api';

export async function fetchPortfolioScorecard(): Promise<{ brands: import('../types').BrandScore[]; generatedInMs: number }> {
  const res = await fetch(`${API_BASE}/portfolio/scorecard`);
  if (!res.ok) throw new Error(`Failed to fetch portfolio scorecard: ${res.status}`);
  return res.json();
}

export async function fetchBrandScore(brandName: string): Promise<import('../types').BrandScore> {
  const res = await fetch(`${API_BASE}/portfolio/brand/${encodeURIComponent(brandName)}`);
  if (!res.ok) throw new Error(`Failed to fetch brand score: ${res.status}`);
  return res.json();
}

export async function fetchExplanation(traceId: string): Promise<import('../types').ExplanationData> {
  const res = await fetch(`${API_BASE}/explain/${traceId}`);
  if (!res.ok) throw new Error(`Failed to fetch explanation: ${res.status}`);
  return res.json();
}
