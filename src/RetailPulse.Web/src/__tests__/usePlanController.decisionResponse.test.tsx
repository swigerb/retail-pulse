import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';

// #144 follow-up: `usePlanController` MUST consume the persisted-winner
// `kind` returned from POST /decision instead of the locally-hard-coded
// requested kind. On a concurrent race the durable ApprovalResult.Decision
// (and therefore the returned response's `kind`) can differ from what this
// caller asked for; using the response's `kind` eliminates any event/local
// order drift with the SignalR `plan_review_resolved` broadcast.
//
// Also covers the repeated-clarification user-visibility path: a resumed
// plan that opens another clarification MUST broadcast the existing
// `approval_requested` event and the controller MUST render
// `PlanClarificationCard` from it.

const answerPlanClarificationMock = vi.fn();
const fetchPlanDetailMock = vi.fn();
const fetchPlanReviewsMock = vi.fn();
const decidePlanReviewMock = vi.fn();
const deletePlanMock = vi.fn();
const fetchPlansMock = vi.fn();

vi.mock('../services/planApi', () => ({
  answerPlanClarification: (...args: unknown[]) => answerPlanClarificationMock(...args),
  fetchPlanDetail: (...args: unknown[]) => fetchPlanDetailMock(...args),
  fetchPlanReviews: (...args: unknown[]) => fetchPlanReviewsMock(...args),
  decidePlanReview: (...args: unknown[]) => decidePlanReviewMock(...args),
  deletePlan: (...args: unknown[]) => deletePlanMock(...args),
  fetchPlans: (...args: unknown[]) => fetchPlansMock(...args),
  parseClarificationPrompt: (raw: string | null) => {
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw);
      return {
        planId: parsed.planId ?? '',
        stepIndex: parsed.stepIndex ?? 0,
        specialistKey: parsed.specialistKey ?? '',
        question: parsed.question ?? '',
      };
    } catch {
      return null;
    }
  },
  parseReviewProposal: () => null,
}));

vi.mock('../services/executionControlApi', () => ({
  reconcilePlan: vi.fn().mockResolvedValue(null),
}));

import { usePlanController, type PlanControllerConnection } from '../state/usePlanController';
import type { PlanReviewDecisionResponse, PlanReviewStep } from '../types';

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
      return () => bucket?.delete(handler);
    },
    onReconnected: () => () => {},
    emit: (event, payload) => {
      const bucket = handlers.get(event);
      if (!bucket) return;
      for (const h of bucket) h(payload);
    },
  };
}

async function primePlanWithReview(planId: string) {
  fetchPlanDetailMock.mockResolvedValueOnce({
    planId,
    sessionId: 'sess-1',
    tenantId: null,
    request: 'q',
    status: 'awaiting_review',
    detectedIntents: ['scorecard'],
    failureReason: null,
    totalInputTokens: null,
    totalOutputTokens: null,
    totalTokens: null,
    totalDurationMs: null,
    createdAt: new Date(0).toISOString(),
    updatedAt: new Date(0).toISOString(),
    steps: [],
  });
  const connection = makeConnection();
  const { result } = renderHook(() => usePlanController({ connection }));
  await act(async () => {
    await result.current.startPlan({
      planId,
      sessionId: 'sess-1',
      request: 'q',
    });
  });
  act(() => {
    connection.emit('approval_requested', {
      id: 'req-1',
      planId,
      context: {
        planId,
        userId: 'user-1',
        kind: 'plan_review',
        roundNumber: 0,
        payload: null,
      },
    });
  });
  return { result, connection };
}

describe('usePlanController consumes returned decision kind (#144 follow-up)', () => {
  beforeEach(() => {
    fetchPlanDetailMock.mockReset();
    fetchPlanReviewsMock.mockReset();
    decidePlanReviewMock.mockReset();
    answerPlanClarificationMock.mockReset();
    deletePlanMock.mockReset();
    fetchPlansMock.mockReset();
  });

  it('approve consumes the returned PlanReviewDecisionResponse (not the requested kind)', async () => {
    const { result } = await primePlanWithReview('p-race');

    const raceResponse: PlanReviewDecisionResponse = {
      requestId: 'req-1',
      planId: 'p-race',
      decision: 'rejected',
      kind: 'reject',
      comment: null,
      respondedAt: new Date().toISOString(),
      terminalReason: 'HumanRejected',
      round: 0,
      feedback: 'the-persisted-feedback',
    };
    decidePlanReviewMock.mockResolvedValueOnce(raceResponse);

    await act(async () => {
      await result.current.approve('go');
    });

    expect(decidePlanReviewMock).toHaveBeenCalledWith(
      'p-race',
      'req-1',
      expect.objectContaining({ kind: 'approve' }),
    );
    expect(result.current.active?.review?.decisionInFlight).toBeUndefined();
  });

  it('reject/edit similarly consume the returned response.kind', async () => {
    const rejectResponse: PlanReviewDecisionResponse = {
      requestId: 'req-1',
      planId: 'p-a',
      decision: 'rejected',
      kind: 'reject',
      comment: null,
      respondedAt: new Date().toISOString(),
      terminalReason: 'HumanRejected',
      round: 0,
    };
    decidePlanReviewMock.mockResolvedValueOnce(rejectResponse);
    const { result: r1 } = await primePlanWithReview('p-a');
    await act(async () => {
      await r1.current.reject('please revise');
    });
    expect(decidePlanReviewMock).toHaveBeenLastCalledWith(
      'p-a',
      'req-1',
      expect.objectContaining({ kind: 'reject', feedback: 'please revise' }),
    );

    const editResponse: PlanReviewDecisionResponse = {
      requestId: 'req-1',
      planId: 'p-b',
      decision: 'modified',
      kind: 'edit',
      comment: null,
      respondedAt: new Date().toISOString(),
      terminalReason: 'HumanModified',
      round: 0,
    };
    decidePlanReviewMock.mockResolvedValueOnce(editResponse);
    const { result: r2 } = await primePlanWithReview('p-b');
    const editedSteps: PlanReviewStep[] = [
      { specialistKey: 'scorecard', intent: 's', action: 'a' },
    ];
    await act(async () => {
      await r2.current.edit(editedSteps);
    });
    expect(decidePlanReviewMock).toHaveBeenLastCalledWith(
      'p-b',
      'req-1',
      expect.objectContaining({ kind: 'edit', editedSteps }),
    );
  });
});

describe('usePlanController opens clarification card on subsequent approval_requested (#144 follow-up)', () => {
  beforeEach(() => {
    fetchPlanDetailMock.mockReset();
    fetchPlanReviewsMock.mockReset();
    decidePlanReviewMock.mockReset();
    answerPlanClarificationMock.mockReset();
    deletePlanMock.mockReset();
    fetchPlansMock.mockReset();
  });

  it('opens PlanClarificationCard when the backend broadcasts approval_requested for a repeat clarification', async () => {
    fetchPlanDetailMock.mockResolvedValueOnce({
      planId: 'p-repeat',
      sessionId: 'sess-1',
      tenantId: null,
      request: 'q',
      status: 'awaiting_clarification',
      detectedIntents: ['scorecard'],
      failureReason: null,
      totalInputTokens: null,
      totalOutputTokens: null,
      totalTokens: null,
      totalDurationMs: null,
      createdAt: new Date(0).toISOString(),
      updatedAt: new Date(0).toISOString(),
      steps: [],
    });
    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));
    await act(async () => {
      await result.current.startPlan({
        planId: 'p-repeat',
        sessionId: 'sess-1',
        request: 'q',
      });
    });

    const clarificationPayload = JSON.stringify({
      planId: 'p-repeat',
      stepIndex: 3,
      specialistKey: 'demand-forecasting',
      question: 'Which forecast horizon?',
    });
    act(() => {
      connection.emit('approval_requested', {
        id: 'clar-2',
        planId: 'p-repeat',
        context: {
          planId: 'p-repeat',
          userId: 'user-1',
          kind: 'clarification',
          roundNumber: 0,
          payload: clarificationPayload,
        },
      });
    });

    expect(result.current.active?.clarification).toBeDefined();
    expect(result.current.active?.clarification?.requestId).toBe('clar-2');
    expect(result.current.active?.clarification?.prompt?.question).toBe(
      'Which forecast horizon?',
    );
    expect(result.current.active?.clarification?.prompt?.specialistKey).toBe(
      'demand-forecasting',
    );
  });
});
