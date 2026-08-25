import type {
  ChartSpec,
  PlanClarificationPrompt,
  PlanDetail,
  PlanReviewProposal,
  PlanStatus,
  PlanStep,
  PlanStepStatus,
  PlanSummary,
  PlanReviewStep,
} from '../types';

/**
 * Plan state store (issue #96). Kept as a plain reducer + shape so it can be
 * unit-tested without React, then plugged into a React `useReducer` at the
 * Dashboard level. The store models one active plan at a time (the plan the
 * user is currently steering) plus a history list for reopening prior plans.
 *
 * All step updates are IDEMPOTENT: applying the same step transition twice
 * yields the same state, and a stale ordering ("running" arriving after
 * "completed" for the same step) does not regress the terminal status. This
 * matches the honesty guarantee the persisted `PlanStepStatus` contract
 * gives — the reducer must never lie about what happened.
 */

export interface ActivePlanState {
  planId: string;
  /** Session that produced the plan; used to route SignalR events. */
  sessionId?: string | null;
  /** Original user request. */
  request: string;
  status: PlanStatus;
  steps: PlanStep[];
  detectedIntents: string[];
  createdAt: string;
  updatedAt: string;
  totalTokens?: number | null;
  totalDurationMs?: number | null;
  failureReason?: string | null;

  /** Wall-clock ms since the plan started (updated by a ticker in the UI). */
  elapsedMs: number;
  /** Timestamp we started counting from (either backend createdAt or client-side). */
  startedAt: number;
  /** When set, elapsed stops advancing at this instant. */
  finishedAt?: number;

  /** Optional review round awaiting the user. */
  review?: {
    requestId: string;
    round: number;
    proposal: PlanReviewProposal | null;
    /** The reviewer's decision, once submitted. */
    decisionInFlight?: 'approve' | 'reject' | 'edit';
    resolvedKind?: 'approve' | 'reject' | 'edit';
    revisionReason?: string | null;
  };

  /** Optional clarification prompt awaiting the user. */
  clarification?: {
    requestId: string;
    prompt: PlanClarificationPrompt | null;
    submitting?: boolean;
  };

  /** Terminal reply text once the plan settles. */
  finalReply?: string;
  terminalReason?: string | null;
  /**
   * Aggregate charts attached to the plan's terminal response (issue #141).
   * Populated on both plan paths: the immediate path forwards
   * `ChatResponse.charts`, and the review-resume path forwards the
   * `plan_final_response` event's `charts` payload. Rendered by `PlanView`
   * near `finalReply` so specialist charts survive the plan boundary.
   */
  finalCharts?: ChartSpec[] | null;

  /** Set true when the SignalR connection drops mid-plan. */
  connectionLost?: boolean;

  /** Error that occurred while hydrating this plan (fetch or parse). */
  hydrateError?: string;
}

export interface PlanAppState {
  active: ActivePlanState | null;
  history: PlanSummary[];
  historyLoading: boolean;
  historyError?: string;
}

export const initialPlanState: PlanAppState = {
  active: null,
  history: [],
  historyLoading: false,
};

// ── Actions ─────────────────────────────────────────────────────────────────

export type PlanAction =
  | {
      type: 'PLAN_STARTED';
      planId: string;
      sessionId?: string | null;
      request: string;
      status?: PlanStatus;
      startedAt?: number;
    }
  | { type: 'PLAN_HYDRATED'; detail: PlanDetail }
  | { type: 'PLAN_HYDRATE_FAILED'; planId: string; error: string }
  | {
      type: 'STEP_STATUS_UPDATED';
      planId: string;
      stepIndex: number;
      status: PlanStepStatus;
      /** Optional monotonic sequence used to reject stale updates. */
      timestamp?: number;
      specialistKey?: string;
    }
  | {
      type: 'REVIEW_REQUESTED';
      planId: string;
      requestId: string;
      round: number;
      proposal: PlanReviewProposal | null;
      revisionReason?: string | null;
    }
  | {
      type: 'REVIEW_DECISION_INFLIGHT';
      planId: string;
      requestId: string;
      kind: 'approve' | 'reject' | 'edit';
    }
  | {
      type: 'REVIEW_DECISION_FAILED';
      planId: string;
      requestId: string;
    }
  | {
      type: 'REVIEW_RESOLVED';
      planId: string;
      requestId: string;
      kind: 'approve' | 'reject' | 'edit';
      terminalReason?: string | null;
    }
  | {
      type: 'CLARIFICATION_REQUESTED';
      planId: string;
      requestId: string;
      prompt: PlanClarificationPrompt | null;
    }
  | { type: 'CLARIFICATION_SUBMITTING'; planId: string; requestId: string }
  | { type: 'CLARIFICATION_SUBMIT_FAILED'; planId: string; requestId: string }
  | { type: 'CLARIFICATION_RESOLVED'; planId: string; requestId: string }
  | {
      type: 'PLAN_FINAL';
      planId: string;
      reply: string;
      terminalReason?: string | null;
      /** Aggregate charts attached to the terminal reply. */
      charts?: ChartSpec[] | null;
    }
  | { type: 'CONNECTION_STATUS'; connected: boolean }
  | {
      // Reconciled delta after a hub reconnect (issue #92): merges any
      // durable step records the client did not render while offline into
      // the active plan without regressing terminal steps and refreshes
      // the plan header from the durable snapshot.
      type: 'STEPS_RECONCILED';
      planId: string;
      steps: readonly PlanStep[];
      status?: PlanStatus;
      updatedAt?: string;
      failureReason?: string | null;
    }
  | { type: 'ELAPSED_TICK'; now: number }
  | { type: 'CLOSE_ACTIVE' }
  | { type: 'HISTORY_LOADING' }
  | { type: 'HISTORY_LOADED'; plans: PlanSummary[] }
  | { type: 'HISTORY_ERROR'; error: string }
  | { type: 'HISTORY_PLAN_REMOVED'; planId: string };

// ── Reducer ─────────────────────────────────────────────────────────────────

const RUNNING_STATUSES: readonly PlanStatus[] = [
  'draft',
  'running',
  'awaiting_review',
  'awaiting_clarification',
];

const TERMINAL_STEP_STATUSES: readonly PlanStepStatus[] = [
  'completed',
  'failed',
  'cancelled',
  'timed_out',
  'skipped',
  'unusable',
];

export function isPlanRunning(status: PlanStatus | undefined | null): boolean {
  return status != null && (RUNNING_STATUSES as readonly string[]).includes(status);
}

export function isStepTerminal(status: PlanStepStatus | undefined | null): boolean {
  return status != null && (TERMINAL_STEP_STATUSES as readonly string[]).includes(status);
}

function detailToActive(detail: PlanDetail, base: ActivePlanState | null): ActivePlanState {
  const startedAt =
    base?.startedAt ?? ((new Date(detail.createdAt).getTime() || Date.now()));
  const finishedAt = isPlanRunning(detail.status)
    ? base?.finishedAt
    : (new Date(detail.updatedAt).getTime() || Date.now());
  return {
    planId: detail.planId,
    sessionId: detail.sessionId,
    request: detail.request,
    status: detail.status,
    steps: [...detail.steps].sort((a, b) => a.stepIndex - b.stepIndex),
    detectedIntents: detail.detectedIntents,
    createdAt: detail.createdAt,
    updatedAt: detail.updatedAt,
    totalTokens: detail.totalTokens ?? base?.totalTokens ?? null,
    totalDurationMs: detail.totalDurationMs ?? base?.totalDurationMs ?? null,
    failureReason: detail.failureReason ?? null,
    elapsedMs: base?.elapsedMs ?? 0,
    startedAt,
    finishedAt,
    review: base?.review,
    clarification: base?.clarification,
    finalReply: base?.finalReply,
    terminalReason: base?.terminalReason,
    finalCharts: base?.finalCharts,
    connectionLost: base?.connectionLost,
    hydrateError: undefined,
  };
}

function updateStepStatus(
  active: ActivePlanState,
  stepIndex: number,
  status: PlanStepStatus,
  specialistKey?: string,
): ActivePlanState {
  let touched = false;
  const nextSteps = active.steps.map(step => {
    if (step.stepIndex !== stepIndex) return step;
    touched = true;
    // Idempotent: same status → no-op. Terminal steps do not regress to
    // "pending" or "running" if an out-of-order update arrives.
    if (step.status === status) return step;
    if (isStepTerminal(step.status) && !isStepTerminal(status)) return step;
    return { ...step, status };
  });

  if (!touched && specialistKey) {
    // Backend emitted a step we don't have yet (e.g. plan started but hydrate
    // hasn't landed). Append a synthetic row so the UI shows the target
    // specialist while we wait for the full detail.
    nextSteps.push({
      stepId: `${active.planId}-${stepIndex}`,
      planId: active.planId,
      stepIndex,
      specialistKey,
      intent: specialistKey,
      action: '',
      status,
    });
    nextSteps.sort((a, b) => a.stepIndex - b.stepIndex);
  }

  return { ...active, steps: nextSteps };
}

/** True when every step has reached a terminal state (used to auto-complete). */
function allStepsTerminal(steps: readonly PlanStep[]): boolean {
  return steps.length > 0 && steps.every(s => isStepTerminal(s.status));
}

export function planReducer(state: PlanAppState, action: PlanAction): PlanAppState {
  switch (action.type) {
    case 'PLAN_STARTED': {
      const now = action.startedAt ?? Date.now();
      const active: ActivePlanState = {
        planId: action.planId,
        sessionId: action.sessionId ?? null,
        request: action.request,
        status: action.status ?? 'running',
        steps: [],
        detectedIntents: [],
        createdAt: new Date(now).toISOString(),
        updatedAt: new Date(now).toISOString(),
        elapsedMs: 0,
        startedAt: now,
      };
      return { ...state, active };
    }

    case 'PLAN_HYDRATED': {
      // Only apply hydrate to the currently active plan (or to a plan the user
      // is opening from history — in that case there is no active plan yet).
      const base = state.active?.planId === action.detail.planId ? state.active : null;
      const merged = detailToActive(action.detail, base);
      // Preserve running steps whose status the SignalR stream already advanced
      // past what the DB has flushed — but never regress a terminal step.
      if (base) {
        merged.steps = merged.steps.map(dbStep => {
          const live = base.steps.find(s => s.stepIndex === dbStep.stepIndex);
          if (!live) return dbStep;
          if (isStepTerminal(live.status) && !isStepTerminal(dbStep.status)) {
            return { ...dbStep, status: live.status };
          }
          return dbStep;
        });
      }
      return { ...state, active: merged };
    }

    case 'PLAN_HYDRATE_FAILED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      return { ...state, active: { ...state.active, hydrateError: action.error } };
    }

    case 'STEP_STATUS_UPDATED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const nextActive = updateStepStatus(
        state.active,
        action.stepIndex,
        action.status,
        action.specialistKey,
      );
      // If every step is now terminal AND the plan itself was still marked
      // running, mark it completed (or failed if any step failed) so the
      // header status matches what we can see. Backend will overwrite on the
      // next hydrate — this is a UI-first optimism, never a persisted change.
      let planStatus = nextActive.status;
      let finishedAt = nextActive.finishedAt;
      if (isPlanRunning(planStatus) && allStepsTerminal(nextActive.steps)) {
        const anyFailed = nextActive.steps.some(s => s.status === 'failed' || s.status === 'timed_out' || s.status === 'unusable');
        planStatus = anyFailed ? 'failed' : 'completed';
        finishedAt = Date.now();
      }
      return { ...state, active: { ...nextActive, status: planStatus, finishedAt } };
    }

    case 'REVIEW_REQUESTED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      return {
        ...state,
        active: {
          ...state.active,
          status: 'awaiting_review',
          review: {
            requestId: action.requestId,
            round: action.round,
            proposal: action.proposal,
            revisionReason: action.revisionReason ?? null,
          },
        },
      };
    }

    case 'REVIEW_DECISION_INFLIGHT': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const review = state.active.review;
      if (!review || review.requestId !== action.requestId) return state;
      return {
        ...state,
        active: {
          ...state.active,
          review: { ...review, decisionInFlight: action.kind },
        },
      };
    }

    case 'REVIEW_DECISION_FAILED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const review = state.active.review;
      if (!review || review.requestId !== action.requestId) return state;
      return {
        ...state,
        active: {
          ...state.active,
          review: { ...review, decisionInFlight: undefined },
        },
      };
    }

    case 'REVIEW_RESOLVED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const review = state.active.review;
      if (!review || review.requestId !== action.requestId) return state;
      // Guard against a late HTTP decision response landing after the plan
      // already went terminal via plan_final_response (issue #145 finding 4).
      // If the plan is already carrying a final reply OR sits in a terminal
      // status, we must preserve the terminal snapshot and only reconcile
      // the review handle (clear decisionInFlight, record resolvedKind).
      // Reverting to `running`/`awaiting_review` here would regress a
      // completed / failed / cancelled / unusable plan back into a
      // non-terminal state and break PlanView's terminal rendering.
      const terminalStatuses: readonly PlanStatus[] = [
        'completed',
        'failed',
        'cancelled',
        'unusable',
      ];
      const alreadyTerminal =
        state.active.finalReply !== undefined
        || terminalStatuses.includes(state.active.status);
      if (alreadyTerminal) {
        return {
          ...state,
          active: {
            ...state.active,
            review: {
              ...review,
              decisionInFlight: undefined,
              resolvedKind: action.kind,
            },
          },
        };
      }
      // Approved & edited move the plan back to running (executor resumes);
      // rejected keeps it in awaiting_review until the next round arrives.
      const nextStatus: PlanStatus =
        action.kind === 'reject' ? 'awaiting_review' : 'running';
      return {
        ...state,
        active: {
          ...state.active,
          status: nextStatus,
          review: {
            ...review,
            decisionInFlight: undefined,
            resolvedKind: action.kind,
          },
          terminalReason: action.terminalReason ?? state.active.terminalReason,
        },
      };
    }

    case 'CLARIFICATION_REQUESTED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      return {
        ...state,
        active: {
          ...state.active,
          status: 'awaiting_clarification',
          clarification: {
            requestId: action.requestId,
            prompt: action.prompt,
          },
        },
      };
    }

    case 'CLARIFICATION_SUBMITTING': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const c = state.active.clarification;
      if (!c || c.requestId !== action.requestId) return state;
      return {
        ...state,
        active: { ...state.active, clarification: { ...c, submitting: true } },
      };
    }

    case 'CLARIFICATION_SUBMIT_FAILED': {
      // The clarification POST failed transiently. Clear the in-flight flag so
      // PlanClarificationCard's submit control re-enables and the user can
      // retry. Guard on both planId and requestId so a stale failure for a
      // prior round never corrupts a newer clarification the reducer has
      // already moved on to.
      if (!state.active || state.active.planId !== action.planId) return state;
      const c = state.active.clarification;
      if (!c || c.requestId !== action.requestId) return state;
      return {
        ...state,
        active: { ...state.active, clarification: { ...c, submitting: false } },
      };
    }

    case 'CLARIFICATION_RESOLVED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const c = state.active.clarification;
      if (!c || c.requestId !== action.requestId) return state;
      return {
        ...state,
        active: {
          ...state.active,
          status: 'running',
          clarification: undefined,
        },
      };
    }

    case 'PLAN_FINAL': {
      if (!state.active || state.active.planId !== action.planId) return state;
      const finished = Date.now();
      // Choose a plan status that agrees with what the terminal reason implies.
      const failedReasons = new Set([
        'PlanReviewReplanExhausted',
        'PlanReviewEditedToEmpty',
        'PlanReviewEditInvalid',
        'PlanReviewTimedOut',
        'PlanClarificationInvalid',
      ]);
      const status: PlanStatus = action.terminalReason && failedReasons.has(action.terminalReason)
        ? 'failed'
        : state.active.status === 'awaiting_review'
          ? 'completed'
          : state.active.status === 'awaiting_clarification'
            ? 'completed'
            : state.active.status === 'failed' || state.active.status === 'cancelled' || state.active.status === 'unusable'
              ? state.active.status
              : 'completed';
      // Freshest wins: an explicit chart array on the terminal event overwrites
      // any prior charts. `undefined` (event carried no chart field at all)
      // preserves what the reducer already has so a broadcast without charts
      // never clears a prior attach.
      const nextFinalCharts =
        action.charts === undefined ? state.active.finalCharts : action.charts;
      return {
        ...state,
        active: {
          ...state.active,
          status,
          finalReply: action.reply,
          terminalReason: action.terminalReason ?? null,
          finalCharts: nextFinalCharts,
          finishedAt: finished,
          review: state.active.review
            ? { ...state.active.review, decisionInFlight: undefined }
            : undefined,
          clarification: undefined,
        },
      };
    }

    case 'CONNECTION_STATUS': {
      if (!state.active) return state;
      if (action.connected && !state.active.connectionLost) return state;
      return {
        ...state,
        active: { ...state.active, connectionLost: !action.connected },
      };
    }

    case 'STEPS_RECONCILED': {
      if (!state.active || state.active.planId !== action.planId) return state;
      // Terminal-monotonic merge, keyed by stepIndex. A step already terminal
      // in the rendered state is NEVER overwritten by a non-terminal record
      // — that would let a stale streamed placeholder regress a completed
      // step during reconnect reconciliation. Non-terminal existing steps
      // are always replaced by the incoming record so refreshed durable
      // detail (final result / durationMs / tokens) supersedes the stream
      // placeholder.
      const byIndex = new Map<number, PlanStep>();
      for (const existing of state.active.steps) {
        byIndex.set(existing.stepIndex, existing);
      }
      for (const incoming of action.steps) {
        if (!Number.isFinite(incoming.stepIndex)) continue;
        const existing = byIndex.get(incoming.stepIndex);
        if (!existing) {
          byIndex.set(incoming.stepIndex, incoming);
          continue;
        }
        if (isStepTerminal(existing.status) && !isStepTerminal(incoming.status)) {
          continue;
        }
        byIndex.set(incoming.stepIndex, incoming);
      }
      const mergedSteps = [...byIndex.values()].sort(
        (a, b) => a.stepIndex - b.stepIndex,
      );

      const nextStatus = action.status ?? state.active.status;
      const wasRunning = isPlanRunning(state.active.status);
      const stillRunning = isPlanRunning(nextStatus);
      const finishedAt = wasRunning && !stillRunning && !state.active.finishedAt
        ? Date.now()
        : state.active.finishedAt;

      return {
        ...state,
        active: {
          ...state.active,
          steps: mergedSteps,
          status: nextStatus,
          updatedAt: action.updatedAt ?? state.active.updatedAt,
          failureReason:
            action.failureReason !== undefined
              ? action.failureReason
              : state.active.failureReason,
          finishedAt,
        },
      };
    }

    case 'ELAPSED_TICK': {
      if (!state.active) return state;
      const endMs = state.active.finishedAt ?? action.now;
      const elapsed = Math.max(0, endMs - state.active.startedAt);
      if (elapsed === state.active.elapsedMs) return state;
      return { ...state, active: { ...state.active, elapsedMs: elapsed } };
    }

    case 'CLOSE_ACTIVE':
      return { ...state, active: null };

    case 'HISTORY_LOADING':
      return { ...state, historyLoading: true, historyError: undefined };

    case 'HISTORY_LOADED':
      return { ...state, historyLoading: false, history: action.plans, historyError: undefined };

    case 'HISTORY_ERROR':
      return { ...state, historyLoading: false, historyError: action.error };

    case 'HISTORY_PLAN_REMOVED':
      return {
        ...state,
        history: state.history.filter(p => p.planId !== action.planId),
        active: state.active?.planId === action.planId ? null : state.active,
      };

    default:
      return state;
  }
}

/** Convenience helper for tests: derive whether a step edit is legal. */
export function canEditReview(active: ActivePlanState | null): boolean {
  if (!active?.review) return false;
  if (active.review.decisionInFlight) return false;
  if (active.review.resolvedKind) return false;
  return active.status === 'awaiting_review';
}

/** Convenience helper: pull the effective steps to show for review (edited or proposal). */
export function reviewSteps(active: ActivePlanState | null): PlanReviewStep[] {
  const review = active?.review;
  if (!review?.proposal) {
    return active?.steps.map(s => ({
      specialistKey: s.specialistKey,
      intent: s.intent,
      action: s.action,
    })) ?? [];
  }
  return review.proposal.steps;
}
