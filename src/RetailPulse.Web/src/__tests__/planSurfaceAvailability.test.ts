import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { initialPlanState, planReducer } from '../state/planReducer';
import { fetchPlans, PlanSurfaceUnavailableError } from '../services/planApi';

/**
 * `/api/plans/*` is mapped only when `PlanPersistence:Enabled` is true. With it
 * off the routes do not exist and the API answers 404 — a deliberate
 * configuration, not a failure. The deployed app surfaced that to the user as
 * "Failed to list plans: 404 Unknown error" in the Plans panel.
 *
 * These pin the degradation contract: 404 is translated to a typed sentinel and
 * reduced to a calm "unavailable" state, while every other non-OK status stays a
 * real, visible error.
 */
describe('plan surface availability', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    globalThis.fetch = vi.fn() as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  function respond(status: number, body: unknown = {}) {
    (globalThis.fetch as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: status >= 200 && status < 300,
      status,
      statusText: '',
      headers: { get: () => 'application/json' },
      json: async () => body,
      text: async () => JSON.stringify(body),
    });
  }

  it('translates a 404 into PlanSurfaceUnavailableError', async () => {
    respond(404);
    await expect(fetchPlans()).rejects.toBeInstanceOf(PlanSurfaceUnavailableError);
  });

  it('leaves other failures as ordinary errors', async () => {
    respond(500, { error: 'boom' });
    const err = await fetchPlans().catch((e: unknown) => e);
    expect(err).toBeInstanceOf(Error);
    expect(err).not.toBeInstanceOf(PlanSurfaceUnavailableError);
    expect((err as Error).message).toContain('500');
  });

  it('returns plans normally on success', async () => {
    respond(200, [{ planId: 'p1' }]);
    await expect(fetchPlans()).resolves.toHaveLength(1);
  });

  it('reduces HISTORY_UNAVAILABLE to a non-error empty state', () => {
    const errored = planReducer(initialPlanState, {
      type: 'HISTORY_ERROR',
      error: 'Failed to list plans: 404 Unknown error',
    });
    expect(errored.historyError).toBeDefined();

    const next = planReducer(errored, { type: 'HISTORY_UNAVAILABLE' });

    expect(next.historyUnavailable).toBe(true);
    expect(next.historyError).toBeUndefined();
    expect(next.historyLoading).toBe(false);
    expect(next.history).toEqual([]);
  });

  it('clears the unavailable flag once a load succeeds', () => {
    const unavailable = planReducer(initialPlanState, { type: 'HISTORY_UNAVAILABLE' });
    expect(unavailable.historyUnavailable).toBe(true);

    const loaded = planReducer(unavailable, { type: 'HISTORY_LOADED', plans: [] });
    expect(loaded.historyUnavailable).toBe(false);
  });
});
