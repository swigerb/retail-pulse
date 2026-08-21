import type { PlanStepRecord } from './executionControlApi';

/**
 * Deterministic client-side merge of a client-rendered step list with the
 * durable step records returned by `/api/plans/{planId}/reconcile` after
 * a real-time reconnect (issue #92).
 *
 * Contract:
 * - Steps are keyed by `stepIndex` (stable, monotonic on the backend).
 * - Duplicate `stepIndex` values collapse to a single entry — the
 *   authoritative record is the one whose status is FURTHER along the
 *   lifecycle (`pending` → `running` → terminal). Once a step is in a
 *   terminal state (`succeeded` / `failed` / `cancelled` / `skipped`) it
 *   is never overwritten by a non-terminal update, so a late-arriving
 *   streaming event cannot regress a completed step.
 * - The returned array is sorted ascending by `stepIndex`, has no gaps
 *   removed (gaps are the caller's signal that MORE steps are still
 *   incoming from streaming), and never contains duplicates.
 *
 * Pure function so overlap/no-duplicate/no-gap behavior can be asserted
 * without a running SignalR connection.
 */

/** Ordered lifecycle used to decide which record "wins" on overlap. */
const LIFECYCLE_ORDER: Readonly<Record<string, number>> = Object.freeze({
  pending: 0,
  queued: 0,
  running: 1,
  in_progress: 1,
  waiting_approval: 1,
  awaiting_approval: 1,
  approved: 1,
  suspended: 1,
  succeeded: 2,
  completed: 2,
  failed: 2,
  cancelled: 2,
  canceled: 2,
  skipped: 2,
});

const TERMINAL_STATUSES: ReadonlySet<string> = new Set([
  'succeeded',
  'completed',
  'failed',
  'cancelled',
  'canceled',
  'skipped',
]);

function normaliseStatus(status: string | null | undefined): string {
  return (status ?? '').trim().toLowerCase();
}

export function isTerminalStatus(status: string | null | undefined): boolean {
  return TERMINAL_STATUSES.has(normaliseStatus(status));
}

function lifecycleWeight(status: string | null | undefined): number {
  const key = normaliseStatus(status);
  const weight = LIFECYCLE_ORDER[key];
  return weight ?? 0;
}

/**
 * Merges an already-rendered list with a batch of reconciled records.
 * Duplicate `stepIndex` values are resolved deterministically:
 *  1. If the rendered record is already terminal, it wins (monotonicity).
 *  2. Otherwise, whichever record has the higher lifecycle weight wins.
 *  3. On a tie, the reconciled record wins so refreshed detail/duration
 *     from the durable store overrides a stale streamed placeholder.
 */
export function reconcilePlanSteps<T extends PlanStepRecord>(
  rendered: readonly T[],
  reconciled: readonly T[],
): T[] {
  const merged = new Map<number, T>();

  for (const record of rendered) {
    if (!Number.isFinite(record.stepIndex)) continue;
    merged.set(record.stepIndex, record);
  }

  for (const incoming of reconciled) {
    if (!Number.isFinite(incoming.stepIndex)) continue;
    const existing = merged.get(incoming.stepIndex);
    if (!existing) {
      merged.set(incoming.stepIndex, incoming);
      continue;
    }

    // Terminal-state monotonicity: never regress a completed step.
    if (isTerminalStatus(existing.status) && !isTerminalStatus(incoming.status)) {
      continue;
    }

    const existingWeight = lifecycleWeight(existing.status);
    const incomingWeight = lifecycleWeight(incoming.status);
    if (incomingWeight >= existingWeight) {
      merged.set(incoming.stepIndex, incoming);
    }
  }

  return [...merged.values()].sort((a, b) => a.stepIndex - b.stepIndex);
}

/**
 * Convenience helper for the reconcile-after-reconnect path: given the
 * current rendered index and a reconcile response, returns the new merged
 * step list AND the next cursor (the max stepIndex now known). Callers
 * pass the cursor back on the next reconcile call so the endpoint only
 * returns rows the client has not yet rendered.
 */
export function applyReconciliation<T extends PlanStepRecord>(
  rendered: readonly T[],
  reconciled: readonly T[],
): { steps: T[]; nextAfterStepIndex: number } {
  const merged = reconcilePlanSteps(rendered, reconciled);
  const nextAfterStepIndex = merged.length === 0
    ? -1
    : merged.reduce((max, s) => (s.stepIndex > max ? s.stepIndex : max), -1);
  return { steps: merged, nextAfterStepIndex };
}
