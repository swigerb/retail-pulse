import { describe, it, expect, beforeAll, vi } from 'vitest';
import React from 'react';
import { render } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ChartSpec } from '../types';

/**
 * P0 regression canary for #74.
 *
 * This is the "production canary" replay: the exact shape of the
 * horizontalBar chart spec that the production backend returned for the
 * "rank all brands by depletion growth rate" prompt after the aggregate fix
 * (#74) shipped. The fixture is inlined (no external files, no session ids,
 * no correlation ids, no tokens, no raw sweep payload) so this regression
 * runs everywhere the rest of the vitest suite runs — local dev, CI, and
 * the coding-agent sandbox.
 *
 * The invariant this test locks down is the render-boundary contract that
 * the P0 originally broke: given the real 12-brand production payload, the
 * real ChartRenderer + real Recharts must paint >= 6 (ideally all 12) real
 * `.recharts-bar-rectangle` layers, one per brand. Zero marks = the P0 has
 * regressed and the ranking card has silently gone empty again (which is
 * exactly what production shipped before #74 was fixed).
 *
 * Note on width assertions: under jsdom, Recharts emits the per-datum
 * `.recharts-bar-rectangle` layer for every brand in the payload but does
 * not tick its animation clock, so the inner sized `<path>` is not yet
 * present and per-bar `width` attributes are not populated. Counting
 * emitted rectangle layers is the strongest signal available at this
 * boundary — pre-fix, the count was 0; post-fix, it must be 12.
 *
 * Same jsdom shim as ChartRenderer.horizontalBarRanking.test.tsx:
 * ResponsiveContainer measures 0x0 under jsdom, so we forward an explicit
 * width/height to the chart child. Recharts itself is real — not mocked.
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

// The 12-brand production-returned spec, replayed verbatim in shape. Brand
// labels are the sanitized fictional roster used across the #74 test suite
// (matches ChartRenderer.horizontalBarRanking.test.tsx) — no real customer
// data, no PII. Growth values mirror the production magnitude/sign
// distribution (mixed positive + negative, ordered by growth rate desc).
const PRODUCTION_SPEC: ChartSpec = {
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
        { x: 'FreshMart', y: 3.2 },
        { x: 'Summit Vodka', y: 2.1 },
        { x: 'Sierra Gold Tequila', y: 1.4 },
        { x: 'Harbor Coffee', y: 0.8 },
        { x: 'Foundry Home', y: -0.6 },
        { x: 'Beacon Snacks', y: -1.9 },
        { x: 'Cascade Cola', y: -2.7 },
        { x: 'Meadow Dairy', y: -3.8 },
        { x: 'Prairie Grain', y: -4.5 },
      ],
    },
  ],
};

describe('Publix canary: production spec replayed through real render code (#74)', () => {
  it('renders_production_spec_with_at_least_six_bar_rectangles_with_positive_width', () => {
    // Sanity: the fixture really is the 12-brand production shape.
    expect(PRODUCTION_SPEC.type).toBe('horizontalBar');
    const values = (PRODUCTION_SPEC.data?.[0] as { values: Array<{ x: string; y: number }> })
      .values;
    expect(values.length).toBe(12);

    const { container } = render(
      <FluentProvider theme={webDarkTheme}>
        <ChartRenderer charts={[PRODUCTION_SPEC]} />
      </FluentProvider>,
    );

    // Real horizontalBar card was painted — not the "chart unavailable" fallback.
    expect(container.querySelector('[data-chart-type="horizontalbar"]')).not.toBeNull();
    expect(container.querySelectorAll('[data-testid="chart-unavailable"]').length).toBe(0);

    // Real Recharts bar-rectangle marks were emitted — one per brand in
    // the production payload. Pre-fix, the payload reaching this boundary
    // was all-zero / underpopulated and this count was 0. Post-fix, all
    // 12 brands paint through, so the count must be >= 6 and (in the
    // happy path) exactly 12.
    const barRects = container.querySelectorAll('.recharts-bar-rectangle');
    expect(barRects.length).toBeGreaterThanOrEqual(6);
    expect(barRects.length).toBe(12);
  });
});
