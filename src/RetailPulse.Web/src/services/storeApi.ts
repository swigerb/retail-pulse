const API_BASE = '/api';

export async function fetchStorePerformance(): Promise<import('../types').StorePerformance[]> {
  const res = await fetch(`${API_BASE}/stores/performance`);
  if (!res.ok) throw new Error(`Failed to fetch store performance: ${res.status}`);
  return res.json();
}

export async function fetchPlanogram(storeId: string, category: string): Promise<{ before: import('../types').PlanogramLayout; after: import('../types').PlanogramLayout }> {
  const res = await fetch(`${API_BASE}/stores/${storeId}/planogram?category=${encodeURIComponent(category)}`);
  if (!res.ok) throw new Error(`Failed to fetch planogram: ${res.status}`);
  return res.json();
}

export async function fetchStockoutRisks(): Promise<import('../types').StockoutRisk[]> {
  const res = await fetch(`${API_BASE}/stores/stockout-risks`);
  if (!res.ok) throw new Error(`Failed to fetch stockout risks: ${res.status}`);
  return res.json();
}
