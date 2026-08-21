import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';

// Issue #92 integration: on a hub reconnect (connection.connected transitions
// false -> true) while a plan is still running, the controller must call
// reconcilePlan with the highest KNOWN-terminal stepIndex, merge without
// duplicates or regressed terminal steps, and refresh the plan header from
// the durable snapshot.

const fetchPlanDetailMock = vi.fn();
const fetchPlanReviewsMock = vi.fn();
const decidePlanReviewMock = vi.fn();
const answerPlanClarificationMock = vi.fn();
const deletePlanMock = vi.fn();
const fetchPlansMock = vi.fn();
const reconcilePlanMock = vi.fn();

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
  reconcilePlan: (...args: unknown[]) => reconcilePlanMock(...args),
}));

import { usePlanController, type PlanControllerConnection } from '../state/usePlanController';

function makeConnection(initialConnected: boolean): PlanControllerConnection {
  return {
    connected: initialConnected,
    on: () => () => {},
  };
}

function stepRecord(index: number, status: string, extra: Record<string, unknown> = {}) {
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

function hydratedDetail(steps: Array<{ index: number; status: string }>) {
  return {
    planId: 'p1',
    sessionId: 'sess-1',
    tenantId: null,
    request: 'q',
    status: 'running',
    detectedIntents: ['demand'],
    failureReason: null,
    totalInputTokens: null,
    totalOutputTokens: null,
    totalTokens: null,
    totalDurationMs: null,
    createdAt: new Date(1_000_000).toISOString(),
    updatedAt: new Date(1_500_000).toISOString(),
    steps: steps.map(s => ({
      stepId: `p1-${s.index}`,
      planId: 'p1',
      stepIndex: s.index,
      specialistKey: 'demand-forecasting',
      intent: 'demand',
      action: `run step ${s.index}`,
      status: s.status,
    })),
  };
}

describe('usePlanController reconcile-on-reconnect (issue #92)', () => {
  beforeEach(() => {
    fetchPlanDetailMock.mockReset();
    reconcilePlanMock.mockReset();
  });

  it('does NOT reconcile on the first-mount connect (no prior drop)', async () => {
    fetchPlanDetailMock.mockResolvedValue(hydratedDetail([{ index: 0, status: 'running' }]));

    const connection = makeConnection(true);
    const { result } = renderHook(() => usePlanController({ connection }));
    await act(async () => {
      await result.current.startPlan({ planId: 'p1', sessionId: 'sess-1', request: 'q' });
    });

    expect(reconcilePlanMock).not.toHaveBeenCalled();
  });

  it('calls reconcilePlan on the false -> true edge with the max terminal stepIndex', async () => {
    fetchPlanDetailMock.mockResolvedValue(hydratedDetail([
      { index: 0, status: 'completed' },
      { index: 1, status: 'running' },
    ]));
    reconcilePlanMock.mockResolvedValue({
      planId: 'p1',
      sessionId: 'sess-1',
      status: 'running',
      failureReason: null,
      updatedAt: new Date(2_500_000).toISOString(),
      totalStepCount: 3,
      afterStepIndex: 0,
      steps: [stepRecord(1, 'completed', { durationMs: 1200 }), stepRecord(2, 'running')],
    });

    const connection = makeConnection(true);
    const { result, rerender } = renderHook(
      ({ conn }: { conn: PlanControllerConnection }) => usePlanController({ connection: conn }),
      { initialProps: { conn: connection } },
    );
    await act(async () => {
      await result.current.startPlan({ planId: 'p1', sessionId: 'sess-1', request: 'q' });
    });

    rerender({ conn: makeConnection(false) });
    rerender({ conn: makeConnection(true) });

    await waitFor(() => {
      expect(reconcilePlanMock).toHaveBeenCalledTimes(1);
    });
    expect(reconcilePlanMock).toHaveBeenCalledWith('p1', { afterStepIndex: 0 });

    await waitFor(() => {
      const steps = result.current.active!.steps;
      expect(steps.map(s => s.stepIndex)).toEqual([0, 1, 2]);
      expect(steps[1].status).toBe('completed');
      expect(steps[1].durationMs).toBe(1200);
      expect(steps[2].status).toBe('running');
    });
  });

  it('never regresses a terminal rendered step even if the endpoint reports a lower status', async () => {
    fetchPlanDetailMock.mockResolvedValue(hydratedDetail([
      { index: 0, status: 'completed' },
    ]));
    reconcilePlanMock.mockResolvedValue({
      planId: 'p1',
      sessionId: 'sess-1',
      status: 'running',
      failureReason: null,
      updatedAt: new Date(2_500_000).toISOString(),
      totalStepCount: 1,
      afterStepIndex: 0,
      steps: [stepRecord(0, 'pending')],
    });

    const connection = makeConnection(true);
    const { result, rerender } = renderHook(
      ({ conn }: { conn: PlanControllerConnection }) => usePlanController({ connection: conn }),
      { initialProps: { conn: connection } },
    );
    await act(async () => {
      await result.current.startPlan({ planId: 'p1', sessionId: 'sess-1', request: 'q' });
    });

    rerender({ conn: makeConnection(false) });
    rerender({ conn: makeConnection(true) });

    await waitFor(() => {
      expect(result.current.active!.steps[0].status).toBe('completed');
    });
  });

  it('does NOT reconcile when the active plan is already in a terminal state', async () => {
    fetchPlanDetailMock.mockResolvedValue({
      ...hydratedDetail([{ index: 0, status: 'completed' }]),
      status: 'completed',
    });

    const connection = makeConnection(true);
    const { result, rerender } = renderHook(
      ({ conn }: { conn: PlanControllerConnection }) => usePlanController({ connection: conn }),
      { initialProps: { conn: connection } },
    );
    await act(async () => {
      await result.current.startPlan({ planId: 'p1', sessionId: 'sess-1', request: 'q' });
    });

    rerender({ conn: makeConnection(false) });
    rerender({ conn: makeConnection(true) });

    await new Promise(resolve => setTimeout(resolve, 0));
    expect(reconcilePlanMock).not.toHaveBeenCalled();
  });
});

