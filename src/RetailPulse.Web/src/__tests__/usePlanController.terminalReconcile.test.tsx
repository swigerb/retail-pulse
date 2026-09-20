import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';

// Issue #299 regression: an approved plan that really executed — real tool
// calls, real tokens, real answer — reported Progress 0/N with steps stuck
// Pending and Tokens 0 because live `span_completed` events were missed,
// dropped, or filtered while `plan_final_response` still drove the plan
// header to Completed. Step status reached the UI ONLY through live spans,
// and `hydrate()` was only called from startPlan / openHistoryPlan, so
// nothing reconciled against server truth once the plan reached terminal.
//
// The fix: when the active plan transitions to a terminal status, re-run
// hydrate once so the durable step rows (and their tokens) reconcile the
// live view. PLAN_HYDRATED already merges without regressing terminal live
// progress; this test proves that guarantee is honored AND that the missed
// events are recovered.

const fetchPlanDetailMock = vi.fn();
const fetchPlanReviewsMock = vi.fn();
const decidePlanReviewMock = vi.fn();
const answerPlanClarificationMock = vi.fn();
const deletePlanMock = vi.fn();
const fetchPlansMock = vi.fn();

vi.mock('../services/planApi', () => ({
  answerPlanClarification: (...args: unknown[]) => answerPlanClarificationMock(...args),
  fetchPlanDetail: (...args: unknown[]) => fetchPlanDetailMock(...args),
  fetchPlanReviews: (...args: unknown[]) => fetchPlanReviewsMock(...args),
  decidePlanReview: (...args: unknown[]) => decidePlanReviewMock(...args),
  deletePlan: (...args: unknown[]) => deletePlanMock(...args),
  fetchPlans: (...args: unknown[]) => fetchPlansMock(...args),
  parseClarificationPrompt: () => null,
  parseReviewProposal: () => null,
}));

vi.mock('../services/executionControlApi', () => ({
  reconcilePlan: vi.fn().mockResolvedValue(null),
}));

import { usePlanController, type PlanControllerConnection } from '../state/usePlanController';

type ConnHandler = (payload: unknown) => void;

function makeConnection(): PlanControllerConnection & {
  emit: (evt: string, payload: unknown) => void;
} {
  const handlers = new Map<string, Set<ConnHandler>>();
  return {
    connected: true,
    on: (event, handler) => {
      let bucket = handlers.get(event);
      if (!bucket) {
        bucket = new Set();
        handlers.set(event, bucket);
      }
      bucket.add(handler);
      return () => {
        bucket?.delete(handler);
      };
    },
    emit: (event, payload) => {
      const bucket = handlers.get(event);
      if (!bucket) return;
      for (const h of Array.from(bucket)) h(payload);
    },
  };
}

function baseStep(index: number, status: string, extra: Record<string, unknown> = {}) {
  return {
    stepId: `p1-${index}`,
    planId: 'p1',
    stepIndex: index,
    specialistKey: 'demand-forecasting',
    intent: 'demand',
    action: `run step ${index}`,
    status,
    ...extra,
  };
}

describe('usePlanController terminal reconciliation (issue #299)', () => {
  beforeEach(() => {
    fetchPlanDetailMock.mockReset();
    fetchPlanReviewsMock.mockReset();
    decidePlanReviewMock.mockReset();
    answerPlanClarificationMock.mockReset();
    deletePlanMock.mockReset();
    fetchPlansMock.mockReset();
  });

  it('re-hydrates once when plan_final_response flips status to terminal, recovering missed step completions and tokens', async () => {
    // Initial hydrate: plan is still running with two pending steps and zero
    // rolled-up tokens (mirrors the exact bug snapshot).
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p1',
      sessionId: 'sess-1',
      tenantId: null,
      request: 'compare regions and forecast demand',
      status: 'running',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalTokens: 0,
      totalDurationMs: null,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(1_500_000).toISOString(),
      steps: [baseStep(0, 'pending'), baseStep(1, 'pending')],
    });

    // Second hydrate — after the plan reaches terminal — returns server
    // truth: both steps completed with real tokens. This is the durable row
    // set that missed spans failed to deliver to the live view.
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p1',
      sessionId: 'sess-1',
      tenantId: null,
      request: 'compare regions and forecast demand',
      status: 'completed',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 800,
      totalOutputTokens: 400,
      totalTokens: 1200,
      totalDurationMs: 3400,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(3_000_000).toISOString(),
      steps: [
        baseStep(0, 'completed', {
          inputTokens: 500,
          outputTokens: 250,
          totalTokens: 750,
          durationMs: 2000,
        }),
        baseStep(1, 'completed', {
          inputTokens: 300,
          outputTokens: 150,
          totalTokens: 450,
          durationMs: 1400,
        }),
      ],
    });

    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));
    await act(async () => {
      await result.current.startPlan({
        planId: 'p1',
        sessionId: 'sess-1',
        request: 'compare regions and forecast demand',
      });
    });

    // Sanity — initial live view matches the bug snapshot.
    expect(result.current.active?.status).toBe('running');
    expect(result.current.active?.steps.map(s => s.status)).toEqual([
      'pending',
      'pending',
    ]);
    expect(result.current.active?.steps.every(s => (s.totalTokens ?? 0) === 0)).toBe(true);
    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(1);

    // Simulate the bug: EVERY span_completed event is missed/dropped/filtered.
    // The only thing that arrives is plan_final_response, which flips the
    // header to Completed but leaves both live steps Pending with 0 tokens.
    await act(async () => {
      connection.emit('plan_final_response', {
        planId: 'p1',
        subject: 'user-1',
        reply: 'here is the aggregate answer',
        terminalReason: null,
      });
    });

    // Terminal reconciliation should have kicked off a second hydrate.
    await waitFor(() => {
      expect(fetchPlanDetailMock).toHaveBeenCalledTimes(2);
    });
    expect(fetchPlanDetailMock).toHaveBeenLastCalledWith('p1');

    // Persisted completion + tokens now reconcile the live view.
    await waitFor(() => {
      const steps = result.current.active?.steps ?? [];
      expect(steps.map(s => s.status)).toEqual(['completed', 'completed']);
      expect(steps.map(s => s.totalTokens)).toEqual([750, 450]);
      expect(steps.map(s => s.durationMs)).toEqual([2000, 1400]);
    });

    // Header stays terminal, final reply preserved, no loop.
    expect(result.current.active?.status).toBe('completed');
    expect(result.current.active?.finalReply).toBe('here is the aggregate answer');

    // Give any stray effects a chance to fire; hydrate must not loop.
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(2);
  });

  it('does not re-hydrate while the plan is still running (live progress non-regression)', async () => {
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p2',
      sessionId: 'sess-2',
      tenantId: null,
      request: 'q',
      status: 'running',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalTokens: 0,
      totalDurationMs: null,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(1_500_000).toISOString(),
      steps: [
        { ...baseStep(0, 'pending'), planId: 'p2', stepId: 'p2-0' },
        { ...baseStep(1, 'pending'), planId: 'p2', stepId: 'p2-1' },
      ],
    });

    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));
    await act(async () => {
      await result.current.startPlan({
        planId: 'p2',
        sessionId: 'sess-2',
        request: 'q',
      });
    });

    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(1);

    // A single span arrives — plan stays running. No reconcile fetch.
    await act(async () => {
      connection.emit('span_completed', {
        span: {
          tags: {
            'span.type': 'plan_step',
            'plan.id': 'p2',
            'plan.step_index': '0',
            'plan.step_status': 'completed',
            'plan.step_specialist': 'demand-forecasting',
          },
        },
      });
    });

    expect(result.current.active?.steps[0].status).toBe('completed');
    expect(result.current.active?.status).toBe('running');

    await new Promise(resolve => setTimeout(resolve, 20));
    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(1);
  });

  it('opening a plan from history that is already terminal does not double-hydrate', async () => {
    fetchPlanDetailMock.mockResolvedValue({
      planId: 'p3',
      sessionId: 'sess-3',
      tenantId: null,
      request: 'q',
      status: 'completed',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 100,
      totalOutputTokens: 50,
      totalTokens: 150,
      totalDurationMs: 500,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(2_000_000).toISOString(),
      steps: [
        {
          ...baseStep(0, 'completed', { totalTokens: 150 }),
          planId: 'p3',
          stepId: 'p3-0',
        },
      ],
    });

    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));

    await act(async () => {
      await result.current.openHistoryPlan('p3');
    });

    // openHistoryPlan issues one hydrate; the terminal-reconcile effect
    // must NOT issue a duplicate because the initial fetch already
    // produced a terminal snapshot.
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(1);
    expect(result.current.active?.status).toBe('completed');
    expect(result.current.active?.steps[0].totalTokens).toBe(150);
  });

  it('server hydrate that still returns pending steps after terminal does not re-hydrate on a loop', async () => {
    // Guard against a runaway loop if the server briefly lags the terminal
    // signal: the terminal-reconcile effect must fire AT MOST once per
    // plan even when the fresh hydrate still shows pending steps.
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p4',
      sessionId: 'sess-4',
      tenantId: null,
      request: 'q',
      status: 'running',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalTokens: 0,
      totalDurationMs: null,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(1_500_000).toISOString(),
      steps: [{ ...baseStep(0, 'pending'), planId: 'p4', stepId: 'p4-0' }],
    });
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p4',
      sessionId: 'sess-4',
      tenantId: null,
      request: 'q',
      status: 'completed',
      detectedIntents: ['demand'],
      failureReason: null,
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalTokens: 0,
      totalDurationMs: null,
      createdAt: new Date(1_000_000).toISOString(),
      updatedAt: new Date(2_000_000).toISOString(),
      // Server still reports pending — simulated lag.
      steps: [{ ...baseStep(0, 'pending'), planId: 'p4', stepId: 'p4-0' }],
    });

    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));
    await act(async () => {
      await result.current.startPlan({
        planId: 'p4',
        sessionId: 'sess-4',
        request: 'q',
      });
    });

    await act(async () => {
      connection.emit('plan_final_response', {
        planId: 'p4',
        subject: 'user-1',
        reply: 'done',
        terminalReason: null,
      });
    });

    await waitFor(() => {
      expect(fetchPlanDetailMock).toHaveBeenCalledTimes(2);
    });

    // Even though the reconciled snapshot still shows pending, no third fetch.
    await new Promise(resolve => setTimeout(resolve, 50));
    expect(fetchPlanDetailMock).toHaveBeenCalledTimes(2);
  });
});
