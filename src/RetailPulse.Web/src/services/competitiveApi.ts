import type { CompetitorPricing, MarketShareEntry, CompetitiveThreat, CompetitorOverview } from '../types';

export async function fetchCompetitorPricing(category?: string, region?: string): Promise<CompetitorPricing[]> {
  const params = new URLSearchParams();
  if (category) params.set('category', category);
  if (region) params.set('region', region);
  const res = await fetch(`/api/competitive/pricing?${params}`);
  if (!res.ok) throw new Error(`Failed to fetch pricing: ${res.status}`);
  return res.json();
}

export async function fetchMarketShare(category?: string, region?: string): Promise<MarketShareEntry[]> {
  const params = new URLSearchParams();
  if (category) params.set('category', category);
  if (region) params.set('region', region);
  const res = await fetch(`/api/competitive/market-share?${params}`);
  if (!res.ok) throw new Error(`Failed to fetch market share: ${res.status}`);
  return res.json();
}

export async function fetchThreats(category?: string, region?: string): Promise<CompetitiveThreat[]> {
  const params = new URLSearchParams();
  if (category) params.set('category', category);
  if (region) params.set('region', region);
  const res = await fetch(`/api/competitive/threats?${params}`);
  if (!res.ok) throw new Error(`Failed to fetch threats: ${res.status}`);
  return res.json();
}

export async function fetchCompetitorProfile(name: string): Promise<CompetitorOverview> {
  const res = await fetch(`/api/competitive/competitor/${encodeURIComponent(name)}`);
  if (!res.ok) throw new Error(`Failed to fetch competitor profile: ${res.status}`);
  return res.json();
}

export async function generateResponsePlan(threatId: string): Promise<{ plan: string }> {
  const res = await fetch(`/api/competitive/threats/${threatId}/response-plan`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to generate response plan: ${res.status}`);
  return res.json();
}
