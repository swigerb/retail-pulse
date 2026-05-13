import type { PromoFormData, PromoEvaluation, PromoCampaign } from '../types';

export async function evaluatePromo(data: PromoFormData): Promise<PromoEvaluation> {
  const res = await fetch('/api/taskmodule/promo', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to evaluate promo: ${res.status}`);
  return res.json();
}

export async function fetchExistingCampaigns(): Promise<PromoCampaign[]> {
  const res = await fetch('/api/campaigns');
  if (!res.ok) throw new Error(`Failed to fetch campaigns: ${res.status}`);
  return res.json();
}

export async function submitForApproval(
  formData: PromoFormData,
  evaluation: PromoEvaluation,
): Promise<void> {
  const res = await fetch('/api/taskmodule/promo/submit', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formData, evaluation }),
  });
  if (!res.ok) throw new Error(`Failed to submit for approval: ${res.status}`);
}
