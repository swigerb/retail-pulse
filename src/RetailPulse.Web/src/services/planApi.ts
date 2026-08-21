import type {
  PlanDetail,
  PlanReviewDecisionRequest,
  PlanReviewDecisionResponse,
  PlanReviewOpen,
  PlanReviewProposal,
  PlanSummary,
  PlanClarificationPrompt,
} from '../types';
import { resolveApiUrl } from '../config/apiOrigin';

/**
 * Wraps the reviewer-facing endpoints from `PlanEndpoints` and
 * `PlanReviewEndpoints`. All requests are subject-scoped server-side, so a
 * cross-subject probe returns 404 rather than another user's data.
 */

async function parseErrorBody(res: Response): Promise<string> {
  const contentType = res.headers.get('content-type') ?? '';
  try {
    if (contentType.includes('application/json')) {
      const data = (await res.json()) as unknown;
      if (typeof data === 'string') return data;
      if (data && typeof data === 'object') {
        const obj = data as Record<string, unknown>;
        const msg = obj.error ?? obj.message ?? obj.detail ?? obj.title;
        if (typeof msg === 'string' && msg.length > 0) return msg;
        return JSON.stringify(data);
      }
    } else {
      const text = await res.text();
      if (text) return text;
    }
  } catch {
    /* ignored — fall back to status text below */
  }
  return res.statusText || 'Unknown error';
}

function throwFor(action: string, res: Response, detail: string): never {
  throw new Error(`Failed to ${action}: ${res.status} ${detail}`.trim());
}

export async function fetchPlans(signal?: AbortSignal): Promise<PlanSummary[]> {
  const res = await fetch(resolveApiUrl('/api/plans/'), { signal });
  if (!res.ok) throwFor('list plans', res, await parseErrorBody(res));
  const data: unknown = await res.json();
  return Array.isArray(data) ? (data as PlanSummary[]) : [];
}

export async function fetchPlanDetail(
  planId: string,
  signal?: AbortSignal,
): Promise<PlanDetail | null> {
  const res = await fetch(resolveApiUrl(`/api/plans/${encodeURIComponent(planId)}`), { signal });
  if (res.status === 404) return null;
  if (!res.ok) throwFor('load plan', res, await parseErrorBody(res));
  return (await res.json()) as PlanDetail;
}

export async function fetchPlanReviews(
  planId: string,
  signal?: AbortSignal,
): Promise<PlanReviewOpen[]> {
  const res = await fetch(
    resolveApiUrl(`/api/plans/${encodeURIComponent(planId)}/reviews/`),
    { signal },
  );
  if (res.status === 404) return [];
  if (!res.ok) throwFor('list plan reviews', res, await parseErrorBody(res));
  const data: unknown = await res.json();
  return Array.isArray(data) ? (data as PlanReviewOpen[]) : [];
}

export async function decidePlanReview(
  planId: string,
  requestId: string,
  body: PlanReviewDecisionRequest,
  signal?: AbortSignal,
): Promise<PlanReviewDecisionResponse> {
  const res = await fetch(
    resolveApiUrl(
      `/api/plans/${encodeURIComponent(planId)}/reviews/${encodeURIComponent(requestId)}/decision`,
    ),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal,
    },
  );
  if (!res.ok) throwFor('submit plan review decision', res, await parseErrorBody(res));
  return (await res.json()) as PlanReviewDecisionResponse;
}

export async function answerPlanClarification(
  planId: string,
  requestId: string,
  answer: string,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(
    resolveApiUrl(
      `/api/plans/${encodeURIComponent(planId)}/clarifications/${encodeURIComponent(requestId)}/answer`,
    ),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ answer }),
      signal,
    },
  );
  if (!res.ok) throwFor('submit clarification answer', res, await parseErrorBody(res));
}

export async function deletePlan(planId: string, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch(resolveApiUrl(`/api/plans/${encodeURIComponent(planId)}`), {
    method: 'DELETE',
    signal,
  });
  if (res.status === 404) return false;
  if (!res.ok) throwFor('delete plan', res, await parseErrorBody(res));
  return true;
}

/**
 * Safely deserialize the JSON payload the reviewer endpoint attaches to an
 * approval row. Returns null when the payload is missing, empty, or malformed
 * so callers can render a helpful empty state instead of crashing the panel.
 */
export function parseReviewProposal(payload: string | null | undefined): PlanReviewProposal | null {
  if (!payload) return null;
  try {
    const parsed = JSON.parse(payload) as unknown;
    if (
      parsed &&
      typeof parsed === 'object' &&
      typeof (parsed as { planId?: unknown }).planId === 'string' &&
      Array.isArray((parsed as { steps?: unknown }).steps)
    ) {
      return parsed as PlanReviewProposal;
    }
  } catch {
    /* Malformed JSON — treat as no proposal. */
  }
  return null;
}

/** Same shape as parseReviewProposal but for clarification prompts. */
export function parseClarificationPrompt(
  payload: string | null | undefined,
): PlanClarificationPrompt | null {
  if (!payload) return null;
  try {
    const parsed = JSON.parse(payload) as unknown;
    if (
      parsed &&
      typeof parsed === 'object' &&
      typeof (parsed as { planId?: unknown }).planId === 'string' &&
      typeof (parsed as { stepIndex?: unknown }).stepIndex === 'number' &&
      typeof (parsed as { question?: unknown }).question === 'string'
    ) {
      return parsed as PlanClarificationPrompt;
    }
  } catch {
    /* ignored */
  }
  return null;
}
