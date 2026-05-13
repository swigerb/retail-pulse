import type { ApprovalRequest, ApprovalResponse } from '../types';

export async function fetchPendingApprovals(): Promise<ApprovalRequest[]> {
  const res = await fetch('/api/approvals/pending');
  if (!res.ok) throw new Error(`Failed to fetch pending approvals: ${res.status}`);
  return res.json();
}

export async function fetchApprovalHistory(): Promise<ApprovalRequest[]> {
  const res = await fetch('/api/approvals');
  if (!res.ok) throw new Error(`Failed to fetch approval history: ${res.status}`);
  return res.json();
}

export async function respondToApproval(
  id: string,
  response: ApprovalResponse,
): Promise<void> {
  const res = await fetch(`/api/approvals/${encodeURIComponent(id)}/respond`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(response),
  });
  if (!res.ok) throw new Error(`Failed to respond to approval: ${res.status}`);
}
