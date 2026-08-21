import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';

// Issue #141 regression: charts produced by specialists during plan execution
// must reach the reducer on BOTH plan paths.
//   - Immediate path: ChatPanel calls `planController.applyFinalResponse` with
//     the ChatResponse's aggregate `charts`.
//   - Review-resume path: the backend broadcasts `plan_final_response` (now
//     session-scoped, see PR for #141 backend commit) with an optional
//     `charts` field the frontend must forward through PLAN_FINAL.
// Before the fix the frontend event type, controller handler, and reducer
// state all discarded `charts`, so the aggregate visualization silently
// vanished on both paths.

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
  parseClarificationPrompt: () => null,
  parseReviewProposal: () => null,
}));

vi.mock('../services/executionControlApi', () => ({
  reconcilePlan: vi.fn().mockResolvedValue(null),
}));

import { usePlanController, type PlanControllerConnection } from '../state/usePlanController';
import type { ChartSpec, ChartType } from '../types';

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

function chartSpec(type: ChartType, title: string): ChartSpec {
  // Bindable single-series spec — the ChartRenderer's own acceptance suite
  // covers per-type visual output; here we only care that the spec survives
  // the controller / reducer boundary intact.
  return {
    type,
    title,
    data: [
      {
        legend: 'series',
        values: [
          { x: 'a', y: 1 },
          { x: 'b', y: 2 },
          { x: 'c', y: 3 },
        ],
      },
    ],
  };
}

const ALL_CHART_TYPES: readonly ChartType[] = [
  'line',
  'bar',
  'groupedBar',
  'stackedBar',
  'horizontalBar',
  'pie',
  'donut',
  'gauge',
  'table',
];

async function primePlan(planId: string) {
  fetchPlanDetailMock.mockResolvedValueOnce(null);
  const connection = makeConnection();
  const { result } = renderHook(() => usePlanController({ connection }));
  await act(async () => {
    await result.current.startPlan({
      planId,
      sessionId: 'sess-1',
      request: 'compare regions and forecast demand',
    });
  });
  return { result, connection };
}

describe('usePlanController charts on plan_final_response (#141)', () => {
  beforeEach(() => {
    fetchPlanDetailMock.mockReset();
    fetchPlanReviewsMock.mockReset();
    decidePlanReviewMock.mockReset();
    answerPlanClarificationMock.mockReset();
    deletePlanMock.mockReset();
    fetchPlansMock.mockReset();
  });

  it('immediate path: applyFinalResponse dispatches reply + charts into finalCharts', async () => {
    const { result } = await primePlan('p-imm');

    const charts = [chartSpec('bar', 'Regional revenue'), chartSpec('line', 'Weekly trend')];

    act(() => {
      result.current.applyFinalResponse({
        planId: 'p-imm',
        reply: 'here is the aggregate answer',
        charts,
      });
    });

    expect(result.current.active?.finalReply).toBe('here is the aggregate answer');
    expect(result.current.active?.finalCharts).toEqual(charts);
    // Chart identity is preserved — the reducer forwards the array by
    // reference; the immediate path never round-trips through JSON.
    expect(result.current.active?.finalCharts?.[0]).toBe(charts[0]);
  });

  it('review-resume path: plan_final_response event with charts hydrates finalCharts', async () => {
    const { result, connection } = await primePlan('p-rev');

    const charts = [chartSpec('groupedBar', 'Category share'), chartSpec('pie', 'Mix')];

    act(() => {
      connection.emit('plan_final_response', {
        planId: 'p-rev',
        subject: 'user-1',
        reply: 'reviewed and executed',
        terminalReason: null,
        charts,
      });
    });

    expect(result.current.active?.finalReply).toBe('reviewed and executed');
    expect(result.current.active?.finalCharts).toEqual(charts);
  });

  it('preserves all nine ChartSpec types across the reducer boundary', async () => {
    const { result, connection } = await primePlan('p-nine');

    const charts = ALL_CHART_TYPES.map(t => chartSpec(t, `chart-${t}`));

    act(() => {
      connection.emit('plan_final_response', {
        planId: 'p-nine',
        reply: 'done',
        charts,
      });
    });

    const rendered = result.current.active?.finalCharts ?? [];
    expect(rendered.map(c => c.type)).toEqual(ALL_CHART_TYPES);
    // Every spec's structure survives — title, data, and first datapoint
    // preserved verbatim so the ChartRenderer sees the same input the
    // backend broadcast.
    for (const spec of rendered) {
      expect(spec.title).toBe(`chart-${spec.type}`);
      expect(spec.data[0].values[0]).toEqual({ x: 'a', y: 1 });
    }
  });

  it('event without charts field does not clear a prior attach', async () => {
    const { result, connection } = await primePlan('p-preserve');

    const first = [chartSpec('donut', 'Mix')];
    act(() => {
      result.current.applyFinalResponse({
        planId: 'p-preserve',
        reply: 'first',
        charts: first,
      });
    });
    expect(result.current.active?.finalCharts).toEqual(first);

    // A subsequent broadcast that carries only reply+terminalReason (no
    // `charts` field at all) must not blow away the previously attached
    // chart array — otherwise a benign redelivery would clear the UI.
    act(() => {
      connection.emit('plan_final_response', {
        planId: 'p-preserve',
        reply: 'first',
        terminalReason: null,
      });
    });
    expect(result.current.active?.finalCharts).toEqual(first);
  });

  it('null charts on the broadcast explicitly clears finalCharts', async () => {
    const { result, connection } = await primePlan('p-null');

    act(() => {
      result.current.applyFinalResponse({
        planId: 'p-null',
        reply: 'first',
        charts: [chartSpec('bar', 'a')],
      });
    });
    expect(result.current.active?.finalCharts?.length).toBe(1);

    act(() => {
      connection.emit('plan_final_response', {
        planId: 'p-null',
        reply: 'no-charts',
        charts: null,
      });
    });
    expect(result.current.active?.finalCharts).toBeNull();
  });

  it('ignores plan_final_response for a different plan', async () => {
    const { result, connection } = await primePlan('p-a');

    act(() => {
      connection.emit('plan_final_response', {
        planId: 'p-b',
        reply: 'unrelated',
        charts: [chartSpec('bar', 'other')],
      });
    });

    expect(result.current.active?.finalReply).toBeUndefined();
    expect(result.current.active?.finalCharts).toBeUndefined();
  });
});
