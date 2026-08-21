import { useCallback, useEffect, useMemo, useReducer, useRef } from 'react';
import type { ActivePlanState, PlanAppState } from './planReducer';
import { initialPlanState, isPlanRunning, planReducer } from './planReducer';
import type {
  PlanClarificationPrompt,
  PlanFinalResponseEvent,
  PlanReviewNextRoundEvent,
  PlanReviewOpen,
  PlanReviewProposal,
  PlanReviewResolvedEvent,
  PlanReviewStep,
  PlanStepStatus,
} from '../types';
import {
  answerPlanClarification,
  decidePlanReview,
  deletePlan as deletePlanApi,
  fetchPlanDetail,
  fetchPlanReviews,
  fetchPlans,
  parseClarificationPrompt,
  parseReviewProposal,
} from '../services/planApi';

/**
 * React binding for the plan reducer. Owns hydration, review/clarification
 * fetch, decision submission, live SignalR wiring, plan history, and the
 * elapsed-time ticker. Kept as a hook so the Dashboard hosts one shared
 * store — ChatPanel and any other consumer read the same state.
 */

export interface PlanControllerConnection {
  /** Register/unregister a callback for the given hub event. */
  on: (event: string, handler: (payload: unknown) => void) => () => void;
  /** True when SignalR is connected. */
  connected: boolean;
}

export interface UsePlanControllerOptions {
  connection: PlanControllerConnection;
}

export interface PlanController {
  state: PlanAppState;
  active: ActivePlanState | null;
  /** Kick off a new plan (called by ChatPanel when the router selects plan). */
  startPlan(input: {
    planId: string;
    sessionId?: string | null;
    request: string;
  }): Promise<void>;
  /** Replace the active plan header from a durable snapshot. */
  hydrate(planId: string): Promise<void>;
  approve(comment?: string): Promise<void>;
  reject(feedback: string): Promise<void>;
  edit(editedSteps: PlanReviewStep[]): Promise<void>;
  clarify(answer: string): Promise<void>;
  close(): void;
  reloadHistory(): Promise<void>;
  removePlanFromHistory(planId: string): Promise<void>;
  openHistoryPlan(planId: string): Promise<void>;
  /** Report a step transition (span_completed / progress event mapper). */
  reportStepStatus(
    planId: string,
    stepIndex: number,
    status: PlanStepStatus,
    specialistKey?: string,
  ): void;
}

const STEP_STATUS_ORDER: Record<PlanStepStatus, number> = {
  pending: 0,
  running: 1,
  cancelled: 2,
  timed_out: 3,
  failed: 4,
  skipped: 5,
  unusable: 6,
  completed: 7,
};

function selectHydratedStatus(current: PlanStepStatus, incoming: PlanStepStatus): PlanStepStatus {
  // Never regress a terminal step. This mirrors the reducer's own guard but
  // lets the hook filter noisy duplicate events before dispatching.
  return STEP_STATUS_ORDER[incoming] >= STEP_STATUS_ORDER[current] ? incoming : current;
}

function normalizeReviewProposal(raw: unknown, planId: string): PlanReviewProposal | null {
  if (!raw || typeof raw !== 'object') return null;
  const value = raw as Record<string, unknown>;
  if (value.planId !== planId && typeof value.planId !== 'string') return null;
  const steps = Array.isArray(value.steps) ? (value.steps as PlanReviewStep[]) : [];
  return {
    planId: typeof value.planId === 'string' ? value.planId : planId,
    roundNumber: typeof value.roundNumber === 'number' ? value.roundNumber : 0,
    request: typeof value.request === 'string' ? value.request : '',
    steps,
    revisionReason:
      typeof value.revisionReason === 'string' ? value.revisionReason : null,
  };
}

function normalizeClarification(raw: unknown, planId: string, stepIndex?: number): PlanClarificationPrompt | null {
  if (!raw || typeof raw !== 'object') return null;
  const value = raw as Record<string, unknown>;
  return {
    planId: typeof value.planId === 'string' ? value.planId : planId,
    stepIndex: typeof value.stepIndex === 'number' ? value.stepIndex : stepIndex ?? 0,
    specialistKey: typeof value.specialistKey === 'string' ? value.specialistKey : '',
    question: typeof value.question === 'string' ? value.question : '',
  };
}

export function usePlanController(options: UsePlanControllerOptions): PlanController {
  const [state, dispatch] = useReducer(planReducer, initialPlanState);
  const activeRef = useRef<ActivePlanState | null>(null);
  activeRef.current = state.active;

  const connectionRef = useRef(options.connection);
  connectionRef.current = options.connection;

  // Elapsed-time ticker — cheap 500ms interval while a plan is running.
  useEffect(() => {
    if (!state.active) return;
    if (!isPlanRunning(state.active.status) && state.active.finishedAt) return;
    const id = setInterval(() => dispatch({ type: 'ELAPSED_TICK', now: Date.now() }), 500);
    return () => clearInterval(id);
  }, [state.active]);

  // Connection status flows into the reducer for the "connection lost" banner.
  useEffect(() => {
    dispatch({ type: 'CONNECTION_STATUS', connected: options.connection.connected });
  }, [options.connection.connected]);

  // Wire SignalR events to reducer actions.
  useEffect(() => {
    const conn = connectionRef.current;

    const disposeApprovalRequested = conn.on('approval_requested', (raw) => {
      const req = raw as {
        id: string;
        planId?: string | null;
        kind?: string;
        context?: { planId?: string; roundNumber?: number; kind?: string; payload?: string };
      };
      const planId = req.context?.planId ?? req.planId ?? null;
      if (!planId) return;
      const active = activeRef.current;
      if (!active || active.planId !== planId) return;
      const kind = req.context?.kind ?? req.kind;
      const payload = req.context?.payload;
      if (kind === 'PlanReview' || kind === 'plan_review') {
        const proposal =
          parseReviewProposal(payload ?? null) ??
          normalizeReviewProposal(payload, planId);
        dispatch({
          type: 'REVIEW_REQUESTED',
          planId,
          requestId: req.id,
          round: req.context?.roundNumber ?? proposal?.roundNumber ?? 0,
          proposal,
          revisionReason: proposal?.revisionReason ?? null,
        });
      } else if (kind === 'Clarification' || kind === 'clarification') {
        const prompt =
          parseClarificationPrompt(payload ?? null) ??
          normalizeClarification(payload, planId);
        dispatch({
          type: 'CLARIFICATION_REQUESTED',
          planId,
          requestId: req.id,
          prompt,
        });
      }
    });

    const disposeNextRound = conn.on('plan_review_next_round', (raw) => {
      const evt = raw as PlanReviewNextRoundEvent;
      const active = activeRef.current;
      if (!active || active.planId !== evt.planId) return;
      // Fetch the fresh proposal payload from the reviews endpoint so the UI
      // can render the amended step list. Fires-and-forgets by design; the
      // reducer stays in awaiting_review until the payload arrives.
      void fetchPlanReviews(evt.planId)
        .then((rows: PlanReviewOpen[]) => {
          const row = rows.find(r => r.requestId === evt.requestId);
          if (!row) return;
          const proposal = parseReviewProposal(row.payload ?? null);
          dispatch({
            type: 'REVIEW_REQUESTED',
            planId: evt.planId,
            requestId: evt.requestId,
            round: evt.round,
            proposal,
            revisionReason: proposal?.revisionReason ?? null,
          });
        })
        .catch(() => {
          dispatch({
            type: 'REVIEW_REQUESTED',
            planId: evt.planId,
            requestId: evt.requestId,
            round: evt.round,
            proposal: null,
          });
        });
    });

    const disposeResolved = conn.on('plan_review_resolved', (raw) => {
      const evt = raw as PlanReviewResolvedEvent;
      dispatch({
        type: 'REVIEW_RESOLVED',
        planId: evt.planId,
        requestId: evt.requestId,
        kind: evt.kind,
        terminalReason: evt.terminalReason,
      });
    });

    const disposeFinal = conn.on('plan_final_response', (raw) => {
      const evt = raw as PlanFinalResponseEvent;
      dispatch({
        type: 'PLAN_FINAL',
        planId: evt.planId,
        reply: evt.reply,
        terminalReason: evt.terminalReason,
      });
    });

    const disposeSpan = conn.on('span_completed', (raw) => {
      const data = raw as { span?: { tags?: Record<string, string> } };
      const tags = data?.span?.tags;
      if (!tags) return;
      if (tags['span.type'] !== 'plan_step') return;
      const planId = tags['plan.id'];
      const stepIndexRaw = tags['plan.step_index'];
      const status = tags['plan.step_status'] as PlanStepStatus | undefined;
      const specialistKey = tags['plan.step_specialist'];
      if (!planId || stepIndexRaw == null || !status) return;
      const stepIndex = Number.parseInt(stepIndexRaw, 10);
      if (Number.isNaN(stepIndex)) return;
      const active = activeRef.current;
      if (!active || active.planId !== planId) return;
      dispatch({
        type: 'STEP_STATUS_UPDATED',
        planId,
        stepIndex,
        status,
        specialistKey,
      });
    });

    return () => {
      disposeApprovalRequested();
      disposeNextRound();
      disposeResolved();
      disposeFinal();
      disposeSpan();
    };
    // Intentionally empty — connection object identity is stable via ref.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hydrate = useCallback(async (planId: string) => {
    try {
      const detail = await fetchPlanDetail(planId);
      if (!detail) {
        dispatch({ type: 'PLAN_HYDRATE_FAILED', planId, error: 'Plan not found' });
        return;
      }
      dispatch({ type: 'PLAN_HYDRATED', detail });
      // If the plan is suspended for review, load the pending proposal too.
      if (detail.status === 'awaiting_review' || detail.status === 'awaiting_clarification') {
        const reviews = await fetchPlanReviews(planId);
        for (const row of reviews) {
          const proposal = parseReviewProposal(row.payload ?? null);
          if (proposal) {
            dispatch({
              type: 'REVIEW_REQUESTED',
              planId,
              requestId: row.requestId,
              round: row.round,
              proposal,
              revisionReason: proposal.revisionReason,
            });
            continue;
          }
          const prompt = parseClarificationPrompt(row.payload ?? null);
          if (prompt) {
            dispatch({
              type: 'CLARIFICATION_REQUESTED',
              planId,
              requestId: row.requestId,
              prompt,
            });
          }
        }
      }
    } catch (err) {
      dispatch({
        type: 'PLAN_HYDRATE_FAILED',
        planId,
        error: err instanceof Error ? err.message : 'Failed to load plan',
      });
    }
  }, []);

  const startPlan = useCallback(
    async (input: { planId: string; sessionId?: string | null; request: string }) => {
      dispatch({
        type: 'PLAN_STARTED',
        planId: input.planId,
        sessionId: input.sessionId,
        request: input.request,
      });
      await hydrate(input.planId);
    },
    [hydrate],
  );

  const approve = useCallback(async (comment?: string) => {
    const active = activeRef.current;
    if (!active?.review) return;
    const requestId = active.review.requestId;
    dispatch({
      type: 'REVIEW_DECISION_INFLIGHT',
      planId: active.planId,
      requestId,
      kind: 'approve',
    });
    try {
      await decidePlanReview(active.planId, requestId, {
        kind: 'approve',
        comment: comment && comment.length > 0 ? comment : undefined,
      });
      dispatch({
        type: 'REVIEW_RESOLVED',
        planId: active.planId,
        requestId,
        kind: 'approve',
      });
    } catch {
      dispatch({
        type: 'REVIEW_DECISION_FAILED',
        planId: active.planId,
        requestId,
      });
    }
  }, []);

  const reject = useCallback(async (feedback: string) => {
    const active = activeRef.current;
    if (!active?.review) return;
    const requestId = active.review.requestId;
    dispatch({
      type: 'REVIEW_DECISION_INFLIGHT',
      planId: active.planId,
      requestId,
      kind: 'reject',
    });
    try {
      await decidePlanReview(active.planId, requestId, {
        kind: 'reject',
        feedback,
      });
      dispatch({
        type: 'REVIEW_RESOLVED',
        planId: active.planId,
        requestId,
        kind: 'reject',
      });
    } catch {
      dispatch({
        type: 'REVIEW_DECISION_FAILED',
        planId: active.planId,
        requestId,
      });
    }
  }, []);

  const edit = useCallback(async (editedSteps: PlanReviewStep[]) => {
    const active = activeRef.current;
    if (!active?.review) return;
    const requestId = active.review.requestId;
    dispatch({
      type: 'REVIEW_DECISION_INFLIGHT',
      planId: active.planId,
      requestId,
      kind: 'edit',
    });
    try {
      await decidePlanReview(active.planId, requestId, {
        kind: 'edit',
        editedSteps,
      });
      dispatch({
        type: 'REVIEW_RESOLVED',
        planId: active.planId,
        requestId,
        kind: 'edit',
      });
    } catch {
      dispatch({
        type: 'REVIEW_DECISION_FAILED',
        planId: active.planId,
        requestId,
      });
    }
  }, []);

  const clarify = useCallback(async (answer: string) => {
    const active = activeRef.current;
    if (!active?.clarification) return;
    const requestId = active.clarification.requestId;
    dispatch({ type: 'CLARIFICATION_SUBMITTING', planId: active.planId, requestId });
    try {
      await answerPlanClarification(active.planId, requestId, answer);
      dispatch({ type: 'CLARIFICATION_RESOLVED', planId: active.planId, requestId });
    } catch {
      // Distinct failure action clears clarification.submitting so the submit
      // control re-enables and the user can retry. Re-dispatching
      // CLARIFICATION_SUBMITTING would leave submitting=true and permanently
      // disable PlanClarificationCard after a transient API failure.
      dispatch({ type: 'CLARIFICATION_SUBMIT_FAILED', planId: active.planId, requestId });
    }
  }, []);

  const close = useCallback(() => {
    dispatch({ type: 'CLOSE_ACTIVE' });
  }, []);

  const reloadHistory = useCallback(async () => {
    dispatch({ type: 'HISTORY_LOADING' });
    try {
      const plans = await fetchPlans();
      dispatch({ type: 'HISTORY_LOADED', plans });
    } catch (err) {
      dispatch({
        type: 'HISTORY_ERROR',
        error: err instanceof Error ? err.message : 'Failed to load plan history',
      });
    }
  }, []);

  const removePlanFromHistory = useCallback(async (planId: string) => {
    try {
      await deletePlanApi(planId);
    } catch {
      // Non-fatal: refresh history to sync with server truth.
    } finally {
      dispatch({ type: 'HISTORY_PLAN_REMOVED', planId });
    }
  }, []);

  const openHistoryPlan = useCallback(
    async (planId: string) => {
      dispatch({ type: 'CLOSE_ACTIVE' });
      // Seed the active plan shell so the PlanView renders during load.
      dispatch({
        type: 'PLAN_STARTED',
        planId,
        request: '',
      });
      await hydrate(planId);
    },
    [hydrate],
  );

  const reportStepStatus = useCallback(
    (planId: string, stepIndex: number, status: PlanStepStatus, specialistKey?: string) => {
      const active = activeRef.current;
      if (!active || active.planId !== planId) return;
      const existing = active.steps.find(s => s.stepIndex === stepIndex);
      const next = existing ? selectHydratedStatus(existing.status, status) : status;
      dispatch({
        type: 'STEP_STATUS_UPDATED',
        planId,
        stepIndex,
        status: next,
        specialistKey,
      });
    },
    [],
  );

  return useMemo<PlanController>(
    () => ({
      state,
      active: state.active,
      startPlan,
      hydrate,
      approve,
      reject,
      edit,
      clarify,
      close,
      reloadHistory,
      removePlanFromHistory,
      openHistoryPlan,
      reportStepStatus,
    }),
    [
      state,
      startPlan,
      hydrate,
      approve,
      reject,
      edit,
      clarify,
      close,
      reloadHistory,
      removePlanFromHistory,
      openHistoryPlan,
      reportStepStatus,
    ],
  );
}

// Re-export types for consumers that don't want to import the reducer.
export type { ActivePlanState, PlanAction, PlanAppState } from './planReducer';
