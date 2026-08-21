import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';

// Issue #141: PlanView is the surface that must render aggregate charts on
// both plan paths. The immediate path pipes `ChatResponse.charts` into
// `finalCharts` via `applyFinalResponse`; the review-resume path pipes
// `plan_final_response.charts` through the reducer's PLAN_FINAL action.
// Both land here as `active.finalCharts`, which this suite proves renders
// through the shared ChartRenderer for every supported ChartType.

vi.mock('../components/ChartRenderer', () => ({
  default: ({ charts }: { charts: Array<{ type: string; title: string }> }) => (
    <div data-testid="chart-renderer-mock">
      {charts.map((c, i) => (
        <div key={i} data-testid="chart-card" data-chart-type={c.type}>
          {c.title}
        </div>
      ))}
    </div>
  ),
}));

import { PlanView } from '../components/plan/PlanView';
import type { ActivePlanState } from '../state/planReducer';
import type { ChartSpec, ChartType, PlanStep } from '../types';

function step(index: number, status: PlanStep['status']): PlanStep {
  return {
    stepId: `s-${index}`,
    planId: 'p1',
    stepIndex: index,
    specialistKey: 'demand-forecasting',
    intent: 'demand',
    action: `run step ${index}`,
    status,
  };
}

function makeActive(overrides: Partial<ActivePlanState> = {}): ActivePlanState {
  return {
    planId: 'p1',
    sessionId: 'sess',
    request: 'demand + promo across regions',
    status: 'completed',
    steps: [step(0, 'completed'), step(1, 'completed')],
    detectedIntents: ['demand', 'promo'],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    elapsedMs: 1234,
    startedAt: Date.now() - 1234,
    finishedAt: Date.now(),
    ...overrides,
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

function chartSpec(type: ChartType, title: string): ChartSpec {
  return {
    type,
    title,
    data: [
      {
        legend: 'series',
        values: [
          { x: 'a', y: 1 },
          { x: 'b', y: 2 },
        ],
      },
    ],
  };
}

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('PlanView aggregate charts (#141)', () => {
  it('does not render the charts section when finalCharts is absent', () => {
    render(
      wrap(
        <PlanView
          active={makeActive({ finalReply: 'aggregate', finalCharts: undefined })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.queryByTestId('plan-final-charts')).not.toBeInTheDocument();
  });

  it('does not render the charts section when finalCharts is an empty array', () => {
    render(
      wrap(
        <PlanView
          active={makeActive({ finalReply: 'aggregate', finalCharts: [] })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.queryByTestId('plan-final-charts')).not.toBeInTheDocument();
  });

  it('renders the charts section with the ChartRenderer when finalCharts is populated', async () => {
    const charts = [chartSpec('bar', 'Region A'), chartSpec('line', 'Trend')];
    render(
      wrap(
        <PlanView
          active={makeActive({ finalReply: 'aggregate', finalCharts: charts })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );

    const section = await screen.findByTestId('plan-final-charts');
    expect(section).toHaveAttribute('data-chart-count', '2');
    await waitFor(() => {
      expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();
    });
    const cards = screen.getAllByTestId('chart-card');
    expect(cards.map(c => c.getAttribute('data-chart-type'))).toEqual(['bar', 'line']);
  });

  it('renders every supported ChartType passed via finalCharts (all 9)', async () => {
    const charts = ALL_CHART_TYPES.map(t => chartSpec(t, `chart-${t}`));
    render(
      wrap(
        <PlanView
          active={makeActive({ finalReply: 'aggregate', finalCharts: charts })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );

    await waitFor(() => {
      expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();
    });
    const cards = screen.getAllByTestId('chart-card');
    expect(cards).toHaveLength(ALL_CHART_TYPES.length);
    expect(cards.map(c => c.getAttribute('data-chart-type'))).toEqual([...ALL_CHART_TYPES]);
  });
});
