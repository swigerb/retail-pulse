import { resolveApiUrl } from '../config/apiOrigin';

/**
 * User-initiated execution control (issue #92). Wraps the two `cancel`
 * endpoints and the plan reconciliation endpoint so components never build
 * fetch payloads by hand.
 *
 * All calls go through `resolveApiUrl` (same-origin under Static Web Apps,
 * direct to the configured ACA origin when `VITE_API_ORIGIN` is set) and
 * pick up the bearer token via the authorized-fetch interceptor.
 */

/**
 * Result of a cancel call so the UI can distinguish "we cancelled it" from
 * "there was nothing to cancel" — both are non-error outcomes, but the
 * second one still means the caller's abort took effect locally.
 */
export type CancelResult = 'cancelled' | 'not_found' | 'forbidden';

export interface CancelOptions {
  readonly signal?: AbortSignal;
}

function encode(segment: string): string {
  return encodeURIComponent(segment);
}

async function postCancel(url: string, options: CancelOptions): Promise<CancelResult> {
  const res = await fetch(url, {
    method: 'POST',
    // The endpoint takes no body; still send the content type header so
    // any intermediary proxy doesn't misclassify the empty POST.
    headers: { 'Content-Type': 'application/json' },
    signal: options.signal,
  });

  if (res.status === 204) return 'cancelled';
  if (res.status === 404) return 'not_found';
  if (res.status === 403) return 'forbidden';

  // Any other status is an unexpected server error; surface a plain message so
  // the caller can decide whether to raise it (e.g. the cancel button UI can
  // ignore this — the local abort already ran).
  throw new Error(`Cancel failed with HTTP ${res.status}`);
}

/**
 * `POST /api/chat/{sessionId}/cancel` — end the caller's in-flight
 * fast-path or streaming chat run. A `not_found` result means either the
 * run has already completed or a foreign session was probed; treat both as
 * a successful no-op from the UI's perspective.
 */
export function cancelChatSession(
  sessionId: string,
  options: CancelOptions = {},
): Promise<CancelResult> {
  if (!sessionId) {
    throw new Error('cancelChatSession requires a non-empty sessionId.');
  }
  return postCancel(resolveApiUrl(`/api/chat/${encode(sessionId)}/cancel`), options);
}

/**
 * `POST /api/plans/{planId}/cancel` — end the caller's in-flight plan
 * orchestration when we know the planId (plan persistence enabled, plan
 * path selected). Anonymous callers cannot own a plan; the backend returns
 * 403 which we surface as `forbidden` so the UI can hide the affordance.
 */
export function cancelPlan(
  planId: string,
  options: CancelOptions = {},
): Promise<CancelResult> {
  if (!planId) {
    throw new Error('cancelPlan requires a non-empty planId.');
  }
  return postCancel(resolveApiUrl(`/api/plans/${encode(planId)}/cancel`), options);
}

/**
 * Durable plan step returned by `/api/plans/{planId}/reconcile`. Field
 * names mirror the wire shape from `PlanStepRecordDto`
 * (`RetailPulse.Contracts/Persistence/PlanDtos.cs`) after the ASP.NET
 * default camelCase serialisation, so the type contract matches what
 * `res.json()` actually delivers — no phantom UI-only fields that would
 * silently be `undefined` on read and be overwritten to `undefined` when
 * a reconciled record replaces a rendered one during merge.
 */
export interface PlanStepRecord {
  readonly stepId?: string;
  readonly planId?: string;
  readonly stepIndex: number;
  readonly specialistKey?: string | null;
  readonly intent?: string | null;
  readonly action?: string | null;
  readonly status: string;
  readonly result?: string | null;
  readonly error?: string | null;
  readonly inputTokens?: number | null;
  readonly outputTokens?: number | null;
  readonly totalTokens?: number | null;
  readonly durationMs?: number | null;
  readonly startedAt?: string | null;
  readonly completedAt?: string | null;
}

export interface PlanReconciliationResponse {
  readonly planId: string;
  readonly sessionId: string;
  readonly status: string;
  readonly failureReason?: string | null;
  readonly updatedAt?: string | null;
  readonly totalStepCount: number;
  readonly afterStepIndex: number;
  readonly steps: readonly PlanStepRecord[];
}

function isPlanStepRecord(v: unknown): v is PlanStepRecord {
  if (!v || typeof v !== 'object') return false;
  const s = v as Record<string, unknown>;
  return typeof s.stepIndex === 'number' && Number.isFinite(s.stepIndex) &&
    typeof s.status === 'string';
}

function isPlanReconciliationResponse(v: unknown): v is PlanReconciliationResponse {
  if (!v || typeof v !== 'object') return false;
  const r = v as Record<string, unknown>;
  return (
    typeof r.planId === 'string' &&
    typeof r.sessionId === 'string' &&
    typeof r.status === 'string' &&
    typeof r.totalStepCount === 'number' &&
    typeof r.afterStepIndex === 'number' &&
    Array.isArray(r.steps) &&
    r.steps.every(isPlanStepRecord)
  );
}

export interface ReconcileOptions {
  readonly afterStepIndex?: number;
  readonly signal?: AbortSignal;
}

/**
 * `GET /api/plans/{planId}/reconcile?afterStepIndex=N` — durable plan
 * status plus any step records whose index exceeds the caller's cursor.
 * Returns `null` when the plan is unknown or belongs to another subject
 * (both collapse to 404 server-side, which is expected on cross-tab races).
 */
export async function reconcilePlan(
  planId: string,
  options: ReconcileOptions = {},
): Promise<PlanReconciliationResponse | null> {
  if (!planId) {
    throw new Error('reconcilePlan requires a non-empty planId.');
  }
  const base = resolveApiUrl(`/api/plans/${encode(planId)}/reconcile`);
  let url = base;
  if (options.afterStepIndex !== undefined && Number.isFinite(options.afterStepIndex)) {
    const value = String(Math.max(-1, Math.trunc(options.afterStepIndex)));
    url = `${base}?afterStepIndex=${value}`;
  }

  const res = await fetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal: options.signal,
  });

  if (res.status === 404 || res.status === 403) return null;
  if (!res.ok) {
    throw new Error(`Plan reconcile failed with HTTP ${res.status}`);
  }

  const data: unknown = await res.json();
  if (!isPlanReconciliationResponse(data)) {
    throw new Error('Plan reconcile returned a malformed payload.');
  }
  return data;
}
