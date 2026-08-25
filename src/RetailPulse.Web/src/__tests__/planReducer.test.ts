import { describe, it, expect } from 'vitest';
import {
  initialPlanState,
  isPlanRunning,
  isStepTerminal,
  planReducer,
} from '../state/planReducer';
import type {
  PlanClarificationPrompt,
  PlanDetail,
  PlanReviewProposal,
  PlanStepStatus,
} from '../types';

function detail(planId: string, steps: Array<{ index: number; status: PlanStepStatus; key?: string }>): PlanDetail {
  return {
    planId,
    sessionId: 'sess-1',
    tenantId: null,
    request: 'compare A and B',
    status: 'running',
    detectedIntents: ['demand', 'promo'],
    failureReason: null,
    totalInputTokens: null,
    totalOutputTokens: null,
    totalTokens: null,
    totalDurationMs: null,
    createdAt: new Date(1_000_000).toISOString(),
    updatedAt: new Date(1_500_000).toISOString(),
    steps: steps.map(s => ({
      stepId: `${planId}-${s.index}`,
      planId,
      stepIndex: s.index,
      specialistKey: s.key ?? 'demand-forecasting',
      intent: 'demand',
      action: `run step ${s.index}`,
      status: s.status,
    })),
  };
}

describe('planReducer', () => {
  it('starts a plan and captures request text', () => {
    const state = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'what should I do about brand X?',
      startedAt: 5_000,
    });
    expect(state.active?.planId).toBe('p1');
    expect(state.active?.status).toBe('running');
    expect(state.active?.startedAt).toBe(5_000);
    expect(state.active?.request).toContain('brand X');
  });

  it('hydrates from a plan detail snapshot preserving startedAt', () => {
    const s1 = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'ask',
      startedAt: 5_000,
    });
    const d = detail('p1', [
      { index: 0, status: 'pending' },
      { index: 1, status: 'pending' },
    ]);
    const s2 = planReducer(s1, { type: 'PLAN_HYDRATED', detail: d });
    expect(s2.active?.steps).toHaveLength(2);
    expect(s2.active?.steps[0].stepIndex).toBe(0);
    expect(s2.active?.startedAt).toBe(5_000);
  });

  it('is idempotent for duplicate step updates', () => {
    const start = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const hydrated = planReducer(start, {
      type: 'PLAN_HYDRATED',
      detail: detail('p1', [
        { index: 0, status: 'pending' },
        { index: 1, status: 'pending' },
      ]),
    });
    const running = planReducer(hydrated, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'running',
    });
    const dup = planReducer(running, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'running',
    });
    // Applying the same status twice does not mutate the step array identity.
    expect(dup.active?.steps[0].status).toBe('running');
  });

  it('does not regress a terminal step to running (out-of-order guard)', () => {
    const hydrated = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'PLAN_HYDRATED',
        detail: detail('p1', [{ index: 0, status: 'pending' }]),
      },
    );
    const completed = planReducer(hydrated, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'completed',
    });
    // Stale "running" event arrives after completion — reducer must ignore it.
    const stale = planReducer(completed, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'running',
    });
    expect(stale.active?.steps[0].status).toBe('completed');
  });

  it('auto-completes the plan when all steps are terminal', () => {
    const hydrated = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'PLAN_HYDRATED',
        detail: detail('p1', [
          { index: 0, status: 'pending' },
          { index: 1, status: 'pending' },
        ]),
      },
    );
    const s1 = planReducer(hydrated, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'completed',
    });
    const s2 = planReducer(s1, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 1,
      status: 'completed',
    });
    expect(s2.active?.status).toBe('completed');
  });

  it('marks plan failed when any step failed and all others are terminal', () => {
    const hydrated = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'PLAN_HYDRATED',
        detail: detail('p1', [
          { index: 0, status: 'pending' },
          { index: 1, status: 'pending' },
        ]),
      },
    );
    const s1 = planReducer(hydrated, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 0,
      status: 'failed',
    });
    const s2 = planReducer(s1, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 1,
      status: 'skipped',
    });
    expect(s2.active?.status).toBe('failed');
  });

  it('appends a synthetic step when a status update arrives ahead of hydrate', () => {
    const started = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const updated = planReducer(started, {
      type: 'STEP_STATUS_UPDATED',
      planId: 'p1',
      stepIndex: 2,
      status: 'running',
      specialistKey: 'promo-planning',
    });
    expect(updated.active?.steps).toHaveLength(1);
    expect(updated.active?.steps[0].stepIndex).toBe(2);
    expect(updated.active?.steps[0].specialistKey).toBe('promo-planning');
  });

  it('opens a review round with the supplied proposal', () => {
    const started = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const proposal: PlanReviewProposal = {
      planId: 'p1',
      roundNumber: 0,
      request: 'q',
      steps: [{ specialistKey: 'demand-forecasting', intent: 'demand', action: 'A' }],
      revisionReason: null,
    };
    const s2 = planReducer(started, {
      type: 'REVIEW_REQUESTED',
      planId: 'p1',
      requestId: 'req-1',
      round: 0,
      proposal,
    });
    expect(s2.active?.status).toBe('awaiting_review');
    expect(s2.active?.review?.requestId).toBe('req-1');
    expect(s2.active?.review?.proposal?.steps).toHaveLength(1);
  });

  it('tracks decision-in-flight and clears it on failure', () => {
    const seed = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'REVIEW_REQUESTED',
        planId: 'p1',
        requestId: 'req-1',
        round: 0,
        proposal: null,
      },
    );
    const inflight = planReducer(seed, {
      type: 'REVIEW_DECISION_INFLIGHT',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'approve',
    });
    expect(inflight.active?.review?.decisionInFlight).toBe('approve');
    const failed = planReducer(inflight, {
      type: 'REVIEW_DECISION_FAILED',
      planId: 'p1',
      requestId: 'req-1',
    });
    expect(failed.active?.review?.decisionInFlight).toBeUndefined();
  });

  it('review approve resolves and marks plan running again', () => {
    const seed = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'REVIEW_REQUESTED',
        planId: 'p1',
        requestId: 'req-1',
        round: 0,
        proposal: null,
      },
    );
    const resolved = planReducer(seed, {
      type: 'REVIEW_RESOLVED',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'approve',
    });
    expect(resolved.active?.status).toBe('running');
    expect(resolved.active?.review?.resolvedKind).toBe('approve');
  });

  it('review reject stays in awaiting_review until next round arrives', () => {
    const seed = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'REVIEW_REQUESTED',
        planId: 'p1',
        requestId: 'req-1',
        round: 0,
        proposal: null,
      },
    );
    const rejected = planReducer(seed, {
      type: 'REVIEW_RESOLVED',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'reject',
    });
    expect(rejected.active?.status).toBe('awaiting_review');
  });

  it('late REVIEW_RESOLVED after PLAN_FINAL preserves terminal status (issue #145 finding 4)', () => {
    // Simulate a plan-final broadcast arriving BEFORE the HTTP POST /decision
    // response resolves — the reducer's REVIEW_RESOLVED action must not
    // regress a plan that already carries `finalReply` back to `running`
    // (approve/edit) or `awaiting_review` (reject).
    const seed = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'REVIEW_REQUESTED',
        planId: 'p1',
        requestId: 'req-1',
        round: 0,
        proposal: null,
      },
    );
    const terminated = planReducer(seed, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'plan already finished',
      terminalReason: null,
    });
    expect(terminated.active?.status).toBe('completed');
    expect(terminated.active?.finalReply).toBe('plan already finished');
    const lateApprove = planReducer(terminated, {
      type: 'REVIEW_RESOLVED',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'approve',
    });
    expect(lateApprove.active?.status).toBe('completed');
    expect(lateApprove.active?.finalReply).toBe('plan already finished');
    expect(lateApprove.active?.review?.resolvedKind).toBe('approve');
    expect(lateApprove.active?.review?.decisionInFlight).toBeUndefined();
    const lateReject = planReducer(terminated, {
      type: 'REVIEW_RESOLVED',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'reject',
    });
    expect(lateReject.active?.status).toBe('completed');
  });

  it('late REVIEW_RESOLVED after PLAN_FINAL with failed reason keeps failed status (issue #145 finding 4)', () => {
    const seed = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      {
        type: 'REVIEW_REQUESTED',
        planId: 'p1',
        requestId: 'req-1',
        round: 0,
        proposal: null,
      },
    );
    const failed = planReducer(seed, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'replan exhausted',
      terminalReason: 'PlanReviewReplanExhausted',
    });
    expect(failed.active?.status).toBe('failed');
    const lateEdit = planReducer(failed, {
      type: 'REVIEW_RESOLVED',
      planId: 'p1',
      requestId: 'req-1',
      kind: 'edit',
    });
    expect(lateEdit.active?.status).toBe('failed');
    expect(lateEdit.active?.finalReply).toBe('replan exhausted');
    expect(lateEdit.active?.review?.resolvedKind).toBe('edit');
  });

  it('opens a clarification round with a parsed prompt', () => {
    const prompt: PlanClarificationPrompt = {
      planId: 'p1',
      stepIndex: 1,
      specialistKey: 'demand-forecasting',
      question: 'which region do you mean?',
    };
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const asked = planReducer(seed, {
      type: 'CLARIFICATION_REQUESTED',
      planId: 'p1',
      requestId: 'req-1',
      prompt,
    });
    expect(asked.active?.status).toBe('awaiting_clarification');
    expect(asked.active?.clarification?.prompt?.question).toBe('which region do you mean?');
  });

  it('clarification submit-failed clears submitting so the user can retry', () => {
    const prompt: PlanClarificationPrompt = {
      planId: 'p1',
      stepIndex: 0,
      specialistKey: 'demand-forecasting',
      question: 'which store?',
    };
    const asked = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      { type: 'CLARIFICATION_REQUESTED', planId: 'p1', requestId: 'req-1', prompt },
    );
    const submitting = planReducer(asked, {
      type: 'CLARIFICATION_SUBMITTING',
      planId: 'p1',
      requestId: 'req-1',
    });
    expect(submitting.active?.clarification?.submitting).toBe(true);
    const failed = planReducer(submitting, {
      type: 'CLARIFICATION_SUBMIT_FAILED',
      planId: 'p1',
      requestId: 'req-1',
    });
    // Regression for #96 blocker: after a rejected answerPlanClarification the
    // submit control must re-enable — submitting is false, prompt is retained
    // so the card still renders the question for another attempt.
    expect(failed.active?.clarification?.submitting).toBe(false);
    expect(failed.active?.clarification?.prompt?.question).toBe('which store?');
    expect(failed.active?.status).toBe('awaiting_clarification');
  });

  it('stale clarification failure does not corrupt a newer clarification round', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const round1 = planReducer(seed, {
      type: 'CLARIFICATION_REQUESTED',
      planId: 'p1',
      requestId: 'req-old',
      prompt: {
        planId: 'p1',
        stepIndex: 0,
        specialistKey: 'demand-forecasting',
        question: 'first?',
      },
    });
    const round1Submitting = planReducer(round1, {
      type: 'CLARIFICATION_SUBMITTING',
      planId: 'p1',
      requestId: 'req-old',
    });
    // The reducer has since advanced to a newer clarification round — this is
    // the "newer clarification" the fix must not corrupt.
    const round2 = planReducer(round1Submitting, {
      type: 'CLARIFICATION_REQUESTED',
      planId: 'p1',
      requestId: 'req-new',
      prompt: {
        planId: 'p1',
        stepIndex: 1,
        specialistKey: 'promo-planning',
        question: 'second?',
      },
    });
    expect(round2.active?.clarification?.requestId).toBe('req-new');
    expect(round2.active?.clarification?.submitting).toBeUndefined();
    // Now the stale failure for req-old arrives — it must be a no-op.
    const staleFailure = planReducer(round2, {
      type: 'CLARIFICATION_SUBMIT_FAILED',
      planId: 'p1',
      requestId: 'req-old',
    });
    expect(staleFailure).toBe(round2);
    expect(staleFailure.active?.clarification?.requestId).toBe('req-new');
    expect(staleFailure.active?.clarification?.submitting).toBeUndefined();
  });

  it('ignores clarification submit-failed for a different active plan', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const asked = planReducer(seed, {
      type: 'CLARIFICATION_REQUESTED',
      planId: 'p1',
      requestId: 'req-1',
      prompt: {
        planId: 'p1',
        stepIndex: 0,
        specialistKey: 'demand-forecasting',
        question: 'which store?',
      },
    });
    const submitting = planReducer(asked, {
      type: 'CLARIFICATION_SUBMITTING',
      planId: 'p1',
      requestId: 'req-1',
    });
    const misroutedFailure = planReducer(submitting, {
      type: 'CLARIFICATION_SUBMIT_FAILED',
      planId: 'other-plan',
      requestId: 'req-1',
    });
    expect(misroutedFailure).toBe(submitting);
    expect(misroutedFailure.active?.clarification?.submitting).toBe(true);
  });

  it('final plan sets terminal reply and stops the elapsed clock', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
      startedAt: 1000,
    });
    const final = planReducer(seed, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'here is the answer',
      terminalReason: null,
    });
    expect(final.active?.finalReply).toBe('here is the answer');
    expect(final.active?.finishedAt).toBeGreaterThan(0);
  });

  it('final terminal reason maps replan-exhausted to failed status', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const final = planReducer(seed, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'stopped after too many replans',
      terminalReason: 'PlanReviewReplanExhausted',
    });
    expect(final.active?.status).toBe('failed');
  });

  it('elapsed tick tracks wall time until the plan finishes', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
      startedAt: 0,
    });
    const tick = planReducer(seed, { type: 'ELAPSED_TICK', now: 2500 });
    expect(tick.active?.elapsedMs).toBe(2500);
  });

  it('connection-lost flag toggles with the CONNECTION_STATUS action', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const down = planReducer(seed, { type: 'CONNECTION_STATUS', connected: false });
    expect(down.active?.connectionLost).toBe(true);
    const up = planReducer(down, { type: 'CONNECTION_STATUS', connected: true });
    expect(up.active?.connectionLost).toBe(false);
  });

  it('STEPS_RECONCILED merges new step records after a reconnect delta', () => {
    // Seed: two rendered steps (0 completed, 1 running) — the reconcile
    // response then delivers a new step 2 the client never saw plus a
    // refreshed durable record for step 1.
    const hydrated = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const withDetail = planReducer(hydrated, {
      type: 'PLAN_HYDRATED',
      detail: detail('p1', [
        { index: 0, status: 'completed' },
        { index: 1, status: 'running' },
      ]),
    });
    const reconciled = planReducer(withDetail, {
      type: 'STEPS_RECONCILED',
      planId: 'p1',
      steps: [
        {
          stepId: 'p1-1',
          planId: 'p1',
          stepIndex: 1,
          specialistKey: 'demand-forecasting',
          intent: 'demand',
          action: 'run step 1',
          status: 'completed',
          durationMs: 1200,
        },
        {
          stepId: 'p1-2',
          planId: 'p1',
          stepIndex: 2,
          specialistKey: 'promo-planning',
          intent: 'promo',
          action: 'run step 2',
          status: 'running',
        },
      ],
      status: 'running',
    });
    const steps = reconciled.active!.steps;
    expect(steps.map(s => s.stepIndex)).toEqual([0, 1, 2]);
    // Non-terminal (running) rendered step 1 is refreshed with the durable
    // completed record, including the new durationMs field.
    expect(steps[1].status).toBe('completed');
    expect(steps[1].durationMs).toBe(1200);
    // Step 2 is added.
    expect(steps[2].specialistKey).toBe('promo-planning');
  });

  it('STEPS_RECONCILED never regresses a terminal rendered step', () => {
    const hydrated = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const withDetail = planReducer(hydrated, {
      type: 'PLAN_HYDRATED',
      detail: detail('p1', [
        { index: 0, status: 'completed' },
      ]),
    });
    // Simulate a stale delta the endpoint should never send but the merge
    // must still be safe against — a 'pending' record for a step the
    // rendered state already saw complete.
    const reconciled = planReducer(withDetail, {
      type: 'STEPS_RECONCILED',
      planId: 'p1',
      steps: [
        {
          stepId: 'p1-0',
          planId: 'p1',
          stepIndex: 0,
          specialistKey: 'demand-forecasting',
          intent: 'demand',
          action: 'run step 0',
          status: 'pending',
        },
      ],
    });
    expect(reconciled.active!.steps[0].status).toBe('completed');
  });

  it('STEPS_RECONCILED refreshes the plan header from the durable snapshot', () => {
    const hydrated = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const withDetail = planReducer(hydrated, {
      type: 'PLAN_HYDRATED',
      detail: detail('p1', [{ index: 0, status: 'running' }]),
    });
    const originalUpdatedAt = withDetail.active!.updatedAt;
    const reconciled = planReducer(withDetail, {
      type: 'STEPS_RECONCILED',
      planId: 'p1',
      steps: [],
      status: 'failed',
      updatedAt: new Date(9_000_000).toISOString(),
      failureReason: 'specialist error',
    });
    expect(reconciled.active!.status).toBe('failed');
    expect(reconciled.active!.updatedAt).not.toBe(originalUpdatedAt);
    expect(reconciled.active!.failureReason).toBe('specialist error');
    expect(reconciled.active!.finishedAt).toBeGreaterThan(0);
  });

  it('STEPS_RECONCILED for a foreign planId is a no-op', () => {
    const hydrated = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const before = planReducer(hydrated, {
      type: 'PLAN_HYDRATED',
      detail: detail('p1', [{ index: 0, status: 'running' }]),
    });
    const after = planReducer(before, {
      type: 'STEPS_RECONCILED',
      planId: 'p2',
      steps: [
        {
          stepId: 'p2-0',
          planId: 'p2',
          stepIndex: 0,
          specialistKey: 'demand-forecasting',
          intent: 'demand',
          action: 'run step 0',
          status: 'completed',
        },
      ],
    });
    expect(after).toBe(before);
  });

  it('history load/error/removal keep the active plan intact', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
    });
    const loading = planReducer(seed, { type: 'HISTORY_LOADING' });
    expect(loading.historyLoading).toBe(true);
    const loaded = planReducer(loading, {
      type: 'HISTORY_LOADED',
      plans: [
        {
          planId: 'p9',
          sessionId: 's',
          tenantId: null,
          request: 'earlier',
          status: 'completed',
          stepCount: 3,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        },
      ],
    });
    expect(loaded.history).toHaveLength(1);
    const removed = planReducer(loaded, { type: 'HISTORY_PLAN_REMOVED', planId: 'p9' });
    expect(removed.history).toHaveLength(0);
    expect(removed.active?.planId).toBe('p1');
  });
});

describe('planReducer selectors', () => {
  it('isPlanRunning covers the four "in-flight" states', () => {
    for (const s of ['draft', 'running', 'awaiting_review', 'awaiting_clarification'] as const) {
      expect(isPlanRunning(s)).toBe(true);
    }
    for (const s of ['completed', 'failed', 'cancelled', 'unusable'] as const) {
      expect(isPlanRunning(s)).toBe(false);
    }
  });

  it('isStepTerminal covers every terminal step status', () => {
    for (const s of ['completed', 'failed', 'cancelled', 'timed_out', 'skipped', 'unusable'] as const) {
      expect(isStepTerminal(s)).toBe(true);
    }
    expect(isStepTerminal('pending')).toBe(false);
    expect(isStepTerminal('running')).toBe(false);
  });
});
