import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';

// Mock the plan API surface the hook talks to. `answerPlanClarification` is
// what the #96 blocker test drives — it must be able to fail transiently and
// leave the submit control re-enabled.
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
  parseClarificationPrompt: (payload: string | null | undefined) =>
    payload ? (JSON.parse(payload) as unknown) : null,
  parseReviewProposal: () => null,
}));

// Import AFTER vi.mock so the mocked planApi wires through.
import { usePlanController, type PlanControllerConnection } from '../state/usePlanController';
import { PlanClarificationCard } from '../components/plan/PlanClarificationCard';

type ConnHandler = (payload: unknown) => void;

function makeConnection(): PlanControllerConnection & { emit: (evt: string, payload: unknown) => void } {
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

function clarificationPayload(planId: string) {
  return JSON.stringify({
    planId,
    stepIndex: 0,
    specialistKey: 'demand-forecasting',
    question: 'Which store are you asking about?',
  });
}

describe('usePlanController.clarify (issue #96 rejected-answer regression)', () => {
  beforeEach(() => {
    answerPlanClarificationMock.mockReset();
    fetchPlanDetailMock.mockReset();
    fetchPlanReviewsMock.mockReset();
    decidePlanReviewMock.mockReset();
    deletePlanMock.mockReset();
    fetchPlansMock.mockReset();
    // hydrate is called by startPlan; return null so we deterministically hit
    // PLAN_HYDRATE_FAILED without going to the network. The active plan stays
    // in state (only hydrateError is set), which is what we need.
    fetchPlanDetailMock.mockResolvedValue(null);
  });

  async function primeClarification() {
    const connection = makeConnection();
    const { result } = renderHook(() => usePlanController({ connection }));

    await act(async () => {
      await result.current.startPlan({ planId: 'p1', sessionId: 'sess-1', request: 'q' });
    });

    act(() => {
      connection.emit('approval_requested', {
        id: 'req-1',
        planId: 'p1',
        kind: 'Clarification',
        context: {
          planId: 'p1',
          kind: 'Clarification',
          payload: clarificationPayload('p1'),
        },
      });
    });

    expect(result.current.active?.status).toBe('awaiting_clarification');
    expect(result.current.active?.clarification?.requestId).toBe('req-1');
    expect(result.current.active?.clarification?.submitting).toBeUndefined();

    return { result, connection };
  }

  it('re-enables the submit control after answerPlanClarification rejects', async () => {
    const { result } = await primeClarification();

    answerPlanClarificationMock.mockRejectedValueOnce(new Error('boom: transient 500'));

    await act(async () => {
      await result.current.clarify('north-store-12');
    });

    await waitFor(() => {
      expect(result.current.active?.clarification?.submitting).toBe(false);
    });

    // The prompt is retained so PlanClarificationCard stays mounted and the
    // user can retry.
    expect(result.current.active?.clarification?.requestId).toBe('req-1');
    expect(result.current.active?.clarification?.prompt?.question).toContain('Which store');
    expect(result.current.active?.status).toBe('awaiting_clarification');

    // The hook did call the API — proves the failure path re-enabled the
    // control, not a silent no-op.
    expect(answerPlanClarificationMock).toHaveBeenCalledTimes(1);
    expect(answerPlanClarificationMock).toHaveBeenCalledWith('p1', 'req-1', 'north-store-12');
  });

  it('a retry after a rejected answer resolves the clarification', async () => {
    const { result } = await primeClarification();

    answerPlanClarificationMock.mockRejectedValueOnce(new Error('boom'));
    await act(async () => {
      await result.current.clarify('north-store-12');
    });
    await waitFor(() => {
      expect(result.current.active?.clarification?.submitting).toBe(false);
    });

    answerPlanClarificationMock.mockResolvedValueOnce(undefined);
    await act(async () => {
      await result.current.clarify('north-store-12');
    });

    await waitFor(() => {
      expect(result.current.active?.clarification).toBeUndefined();
    });
    expect(result.current.active?.status).toBe('running');
    expect(answerPlanClarificationMock).toHaveBeenCalledTimes(2);
  });

  it('renders PlanClarificationCard with an enabled submit after a failure', async () => {
    const { result } = await primeClarification();

    answerPlanClarificationMock.mockRejectedValueOnce(new Error('boom'));
    await act(async () => {
      await result.current.clarify('north-store-12');
    });
    await waitFor(() => {
      expect(result.current.active?.clarification?.submitting).toBe(false);
    });

    const clarification = result.current.active!.clarification!;
    const onAnswer = vi.fn();
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <PlanClarificationCard
          planId="p1"
          requestId={clarification.requestId}
          prompt={clarification.prompt}
          submitting={clarification.submitting}
          onAnswer={onAnswer}
        />
      </FluentProvider>,
    );

    const textarea = screen.getByTestId('plan-clarification-answer') as HTMLTextAreaElement;
    const button = screen.getByTestId('plan-clarification-submit') as HTMLButtonElement;

    const user = userEvent.setup();
    await user.type(textarea, 'north-store-12');

    expect(button.disabled).toBe(false);
    expect(button.textContent).toContain('Send answer');

    await user.click(button);
    expect(onAnswer).toHaveBeenCalledWith('north-store-12');
  });
});
