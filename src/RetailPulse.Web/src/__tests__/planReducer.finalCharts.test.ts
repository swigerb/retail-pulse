import { describe, it, expect } from 'vitest';
import { initialPlanState, planReducer } from '../state/planReducer';
import type { ChartSpec, PlanDetail } from '../types';

/**
 * Issue #137 regression coverage at the reducer boundary. The plan-review
 * resume path relies on `PLAN_FINAL.charts` to persist the specialist
 * chart list into `ActivePlanState.finalCharts`; the immediate plan-first
 * path dispatches the same action from `usePlanController.startPlan`.
 * If the reducer silently discards charts, both the fast-path chart
 * bubble and the resume-path chart bubble go blank.
 *
 * These tests are deterministic and require nothing beyond the reducer —
 * a chart drop in `PLAN_FINAL` fails them immediately.
 */

const NINE_CHARTS: readonly ChartSpec[] = [
  { type: 'line', title: 'A: Depletion trend', data: [{ legend: 'Brand A', values: [{ x: 'W1', y: 100 }, { x: 'W2', y: 120 }] }] },
  { type: 'bar', title: 'B: Velocity by brand', data: [{ legend: 'Velocity', values: [{ x: 'Brand A', y: 900 }, { x: 'Brand B', y: 800 }] }] },
  { type: 'groupedBar', title: 'C: Volume by region', data: [
    { legend: 'Brand A', values: [{ x: 'NE', y: 100 }, { x: 'SE', y: 120 }] },
    { legend: 'Brand B', values: [{ x: 'NE', y: 80 }, { x: 'SE', y: 90 }] },
  ] },
  { type: 'stackedBar', title: 'D: Stacked mix', data: [
    { legend: 'S1', values: [{ x: 'Q1', y: 30 }, { x: 'Q2', y: 40 }] },
    { legend: 'S2', values: [{ x: 'Q1', y: 70 }, { x: 'Q2', y: 60 }] },
  ] },
  { type: 'horizontalBar', title: 'E: Top brands YoY', data: [{ legend: 'YoY %', values: [
    { x: 'Brand A', y: 8.4 }, { x: 'Brand B', y: 6.1 }, { x: 'Brand C', y: 3.2 },
    { x: 'Brand D', y: 1.9 }, { x: 'Brand E', y: -1.2 }, { x: 'Brand F', y: -3.4 },
  ] }] },
  { type: 'pie', title: 'F: Share', data: [{ legend: 'Share %', values: [{ x: 'A', y: 45 }, { x: 'B', y: 35 }, { x: 'C', y: 20 }] }] },
  { type: 'donut', title: 'G: Mix', data: [{ legend: 'Mix %', values: [{ x: 'Original', y: 45 }, { x: 'Spicy', y: 33 }, { x: 'Verde', y: 22 }] }] },
  { type: 'gauge', title: 'H: Inventory health', data: [{ legend: 'Inventory Health', values: [{ x: 'Region', y: 75 }] }] },
  { type: 'table', title: 'I: Ops table', data: [{ legend: 'Depletions %', values: [{ x: 'Row 1', y: 12 }] }] },
];

function detailFor(planId: string): PlanDetail {
  return {
    planId,
    sessionId: 'sess-1',
    tenantId: null,
    request: 'compare A and B',
    status: 'running',
    detectedIntents: ['demand'],
    failureReason: null,
    totalInputTokens: null,
    totalOutputTokens: null,
    totalTokens: null,
    totalDurationMs: null,
    createdAt: new Date(1_000_000).toISOString(),
    updatedAt: new Date(1_500_000).toISOString(),
    steps: [
      { stepId: `${planId}-0`, planId, stepIndex: 0, specialistKey: 'demand-forecasting', intent: 'demand', action: 'run', status: 'completed' },
    ],
  };
}

describe('planReducer PLAN_FINAL charts (issue #137)', () => {
  it('persists all 9 canonical chart types on ActivePlanState.finalCharts in specialist order', () => {
    const seed = planReducer(initialPlanState, {
      type: 'PLAN_STARTED',
      planId: 'p1',
      request: 'q',
      startedAt: 0,
    });

    const final = planReducer(seed, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'here is the aggregate',
      terminalReason: null,
      charts: [...NINE_CHARTS],
    });

    expect(final.active?.finalReply).toBe('here is the aggregate');
    expect(final.active?.finalCharts).toHaveLength(9);
    expect(final.active?.finalCharts?.map((c) => c.type)).toEqual([
      'line', 'bar', 'groupedBar', 'stackedBar', 'horizontalBar', 'pie', 'donut', 'gauge', 'table',
    ]);
    expect(final.active?.finalCharts?.map((c) => c.title)).toEqual(
      NINE_CHARTS.map((c) => c.title),
    );
  });

  it('overwrites prior finalCharts with null when the backend delivers no charts on this turn', () => {
    const withCharts = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      { type: 'PLAN_FINAL', planId: 'p1', reply: 'r', charts: [...NINE_CHARTS] },
    );
    expect(withCharts.active?.finalCharts).toHaveLength(9);

    const rerun = planReducer(withCharts, {
      type: 'PLAN_FINAL',
      planId: 'p1',
      reply: 'r2',
      charts: null,
    });
    expect(rerun.active?.finalCharts).toBeNull();
  });

  it('PLAN_STARTED for a fresh plan does not carry stale finalCharts from a prior plan', () => {
    const p1Done = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q1' }),
      { type: 'PLAN_FINAL', planId: 'p1', reply: 'r', charts: [...NINE_CHARTS] },
    );
    expect(p1Done.active?.finalCharts).toHaveLength(9);

    const p2 = planReducer(p1Done, { type: 'PLAN_STARTED', planId: 'p2', request: 'q2' });
    expect(p2.active?.planId).toBe('p2');
    expect(p2.active?.finalCharts).toBeUndefined();
    expect(p2.active?.finalReply).toBeUndefined();
  });

  it('PLAN_HYDRATED preserves same-plan finalCharts (PlanDetail carries no charts field)', () => {
    const settled = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      { type: 'PLAN_FINAL', planId: 'p1', reply: 'r', charts: [...NINE_CHARTS] },
    );
    const hydrated = planReducer(settled, { type: 'PLAN_HYDRATED', detail: detailFor('p1') });
    expect(hydrated.active?.finalCharts).toHaveLength(9);
    expect(hydrated.active?.finalReply).toBe('r');
  });

  it('PLAN_HYDRATED for a different plan starts with no finalCharts', () => {
    const p1Done = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      { type: 'PLAN_FINAL', planId: 'p1', reply: 'r', charts: [...NINE_CHARTS] },
    );
    // Simulate opening a different plan via hydrate directly.
    const hydrated = planReducer(p1Done, { type: 'PLAN_HYDRATED', detail: detailFor('p2') });
    expect(hydrated.active?.planId).toBe('p2');
    expect(hydrated.active?.finalCharts).toBeUndefined();
  });

  it('CLOSE_ACTIVE clears any settled charts', () => {
    const done = planReducer(
      planReducer(initialPlanState, { type: 'PLAN_STARTED', planId: 'p1', request: 'q' }),
      { type: 'PLAN_FINAL', planId: 'p1', reply: 'r', charts: [...NINE_CHARTS] },
    );
    const closed = planReducer(done, { type: 'CLOSE_ACTIVE' });
    expect(closed.active).toBeNull();
  });
});
