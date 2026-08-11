import { describe, it, expect, beforeAll, vi } from 'vitest';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ChartSpec } from '../types';

/**
 * P0 regression #74: a horizontalBar spec for "rank all brands by depletion
 * growth rate" reached the frontend with either an all-zero payload or a
 * badly-underpopulated payload. `chartIsRenderable` treats 0 as a legal finite
 * datapoint (it is — for gauges), so the ranking chart passed the guard, was
 * dispatched to `RenderHorizontalBarChart`, and Recharts painted an empty
 * shell: axes, no `.recharts-bar-rectangle` marks, no "chart unavailable"
 * diagnostic.
 *
 * These tests lock in the render-boundary contract:
 *
 *   1. A well-formed 12-brand horizontalBar payload paints >= 6 real
 *      `.recharts-bar-rectangle` marks with non-zero width.
 *   2. An all-zero or under-populated horizontalBar payload falls back to the
 *      shared `chart-unavailable` note — never a silent zero-mark card.
 *
 * ResponsiveContainer measures 0x0 under jsdom, so we forward an explicit size
 * to the chart child (same shim as ChartRenderer.comparison.test.tsx and the
 * matrix test).
 */
vi.mock('recharts', async (importOriginal) => {
  const actual = await importOriginal<typeof import('recharts')>();
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactElement }) =>
      React.cloneElement(children as React.ReactElement<Record<string, unknown>>, {
        width: 800,
        height: 600,
      }),
  };
});

import ChartRenderer from '../components/ChartRenderer';

beforeAll(() => {
  if (!(globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver) {
    class RO {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    (globalThis as unknown as { ResizeObserver: typeof RO }).ResizeObserver = RO;
  }
});

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const TWELVE_BRAND_GROWTH: Array<{ x: string; y: number }> = [
  { x: 'Ridgeline Bourbon', y: 8.4 },
  { x: 'Apex Grill', y: 6.1 },
  { x: 'Coastline Tacos', y: 4.7 },
  { x: 'FreshMart', y: 3.2 },
  { x: 'Summit Vodka', y: 2.1 },
  { x: 'Sierra Gold Tequila', y: 1.4 },
  { x: 'Harbor Coffee', y: 0.8 },
  { x: 'Foundry Home', y: -0.6 },
  { x: 'Beacon Snacks', y: -1.9 },
  { x: 'Cascade Cola', y: -2.7 },
  { x: 'Meadow Dairy', y: -3.8 },
  { x: 'Prairie Grain', y: -4.5 },
];

const growthRankingSpec12Brands: ChartSpec = {
  type: 'horizontalBar',
  title: 'Brands Ranked by Depletion Growth Rate (YoY)',
  xAxisTitle: 'Growth %',
  yAxisTitle: 'Brand',
  data: [{ legend: 'Growth %', values: TWELVE_BRAND_GROWTH }],
};

describe('ChartRenderer horizontalBar ranking (issue #74)', () => {
  it('renders_at_least_six_recharts_bar_rectangles_for_growth_ranking', () => {
    const { container } = renderWithProvider(
      <ChartRenderer charts={[growthRankingSpec12Brands]} />,
    );

    // No diagnostic — the chart has real magnitude and paints a real card.
    expect(screen.queryByRole('note')).toBeNull();
    expect(container.querySelector('[data-chart-type="horizontalbar"]')).not.toBeNull();

    // Same assertion shape used by chartAcceptance.matrix.test.tsx: count the
    // `.recharts-bar-rectangle` marks the horizontalBar renderer emits. Under
    // jsdom, Recharts' bar-rectangle layer is created per datapoint even
    // though the animation clock never ticks — the P0 regression is proven
    // by these marks being ZERO on the all-zero path (see the "unavailable"
    // sibling test), and by them being >= 6 here.
    const barRects = container.querySelectorAll('.recharts-bar-rectangle');
    expect(barRects.length).toBeGreaterThanOrEqual(6);

    // And no diagnostic path snuck in alongside the chart card.
    expect(container.querySelectorAll('[data-testid="chart-unavailable"]').length).toBe(0);
  });

  it('renders_chart_unavailable_when_all_marks_are_zero', () => {
    const allZeroSpec: ChartSpec = {
      type: 'horizontalBar',
      title: 'Brands Ranked by Depletion Growth Rate (YoY)',
      xAxisTitle: 'Growth %',
      yAxisTitle: 'Brand',
      data: [
        {
          legend: 'Growth %',
          values: TWELVE_BRAND_GROWTH.map((v) => ({ x: v.x, y: 0 })),
        },
      ],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[allZeroSpec]} />);

    // The diagnostic note is present.
    expect(screen.getByRole('note')).toBeInTheDocument();
    expect(screen.getByText(/Chart unavailable/i)).toBeInTheDocument();

    // And no chart shell was painted — no bar rectangles at all.
    expect(container.querySelectorAll('.recharts-bar-rectangle').length).toBe(0);
    // Belt-and-braces: no `data-chart-type` attribute either.
    expect(container.querySelector('[data-chart-type="horizontalbar"]')).toBeNull();
  });

  it('renders_chart_unavailable_when_ranking_has_fewer_than_six_marks', () => {
    // A "ranking" of 3 brands is a broken aggregate — the ranking builder
    // silently produced too few marks. Fall back to the diagnostic rather
    // than surface a misleading 3-bar chart labelled as an all-brand ranking.
    const underpopulatedSpec: ChartSpec = {
      type: 'horizontalBar',
      title: 'Brands Ranked by Depletion Growth Rate (YoY)',
      xAxisTitle: 'Growth %',
      yAxisTitle: 'Brand',
      data: [
        {
          legend: 'Growth %',
          values: [
            { x: 'Ridgeline Bourbon', y: 8.4 },
            { x: 'Apex Grill', y: 6.1 },
            { x: 'Coastline Tacos', y: 4.7 },
          ],
        },
      ],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[underpopulatedSpec]} />);

    expect(screen.getByRole('note')).toBeInTheDocument();
    expect(container.querySelectorAll('.recharts-bar-rectangle').length).toBe(0);
  });
});
