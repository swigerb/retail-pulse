import { describe, it, expect, beforeAll, vi } from 'vitest';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ChartSpec, ChartSeries } from '../types';
import {
  PROMPT_ACCEPTANCE_CASES,
  type PromptAcceptanceCase,
} from '../components/promptAcceptance';

/**
 * Chart-prompt render acceptance keyed off the extended prompt manifest.
 *
 * `chartAcceptance.matrix.test.tsx` already renders every chart case through
 * real Recharts and asserts marks/legends/entities. This suite is complementary
 * and locks in the acceptance-behavior contract added in issue #58:
 *
 *   • Stable `data-testid` structural selectors (never Griffel class
 *     substrings) — `chart-card`, `chart-title`, `chart-gauge`, `chart-table`.
 *   • `data-chart-type` attribute matches the manifest's expectedVisualization.
 *   • Rendered DOM contains no JSON leakage (no `"type": "bar"` or
 *     `"chart_spec":` blocks).
 *   • Rendered DOM contains no "Chart unavailable" fallback (no `role=note`).
 *   • Render completes under a per-case performance ceiling.
 *
 * Each case builds a representative ChartSpec that satisfies the manifest,
 * mounts `<ChartRenderer />` against real Recharts, and asserts the above.
 */

// Same ResponsiveContainer shim as the matrix test — jsdom measures 0x0 so
// Recharts renders no inner marks unless we forward an explicit size.
vi.mock('recharts', async (importOriginal) => {
  const actual = await importOriginal<typeof import('recharts')>();
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactElement }) =>
      React.cloneElement(children as React.ReactElement<Record<string, unknown>>, {
        width: 800,
        height: 400,
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

const SEEDED_REGIONS = [
  'Northeast',
  'Southeast',
  'Midwest',
  'Southwest',
  'West Coast',
  'Pacific Northwest',
] as const;

/**
 * Build a representative ChartSpec that satisfies the manifest case's shape.
 * Mirrors the fixture shapes used by `chartAcceptance.matrix.test.tsx` so both
 * suites exercise the same production-plausible payloads.
 */
function buildSpec(c: PromptAcceptanceCase): ChartSpec {
  const type = c.expectedVisualization;
  switch (type) {
    case 'line': {
      const brand = c.expectedEntities[0];
      return {
        type: 'line',
        title: `${brand} Depletion Trend by Region`,
        xAxisTitle: 'Region',
        yAxisTitle: 'Depletion Volume',
        data: [
          {
            legend: brand,
            values: SEEDED_REGIONS.map((r, i) => ({ x: r, y: 500 + i * 55 })),
          },
        ],
      };
    }
    case 'bar': {
      return {
        type: 'bar',
        title: 'Depletion Velocity by Brand — Northeast',
        xAxisTitle: 'Brand',
        yAxisTitle: 'Avg Weekly Depletion Velocity',
        data: [
          {
            legend: 'Avg Weekly Depletion Velocity',
            values: c.expectedEntities.map((b, i) => ({ x: b, y: 800 + i * 40 })),
          },
        ],
      };
    }
    case 'pie': {
      const values = c.expectedEntities.map((b, i) => ({ x: b, y: 42 - i * 10 }));
      if (values.length < c.expectedCategories) values.push({ x: 'Other', y: 30 });
      return {
        type: 'pie',
        title: 'Market Share Breakdown',
        data: [{ legend: 'Market Share %', values }],
      };
    }
    case 'donut': {
      return {
        type: 'donut',
        title: `${c.expectedEntities[0]} Variant Mix`,
        data: [
          {
            legend: 'Variant Mix %',
            values: [
              { x: 'Original', y: 45 },
              { x: 'Spicy', y: 33 },
              { x: 'Verde', y: 22 },
            ],
          },
        ],
      };
    }
    case 'groupedBar': {
      const data: ChartSeries[] = c.expectedEntities.map((brand, bi) => ({
        legend: brand,
        values: SEEDED_REGIONS.map((r, ri) => ({ x: r, y: 1000 + bi * 200 + ri * 30 })),
      }));
      return {
        type: 'groupedBar',
        title: `${c.expectedEntities.join(' vs ')} — Depletion Volume by Region`,
        xAxisTitle: 'Region',
        yAxisTitle: 'Depletion Volume',
        data,
      };
    }
    case 'horizontalBar': {
      return {
        type: 'horizontalBar',
        title: 'Brands Ranked by Depletion Growth Rate (YoY)',
        xAxisTitle: 'Depletion Growth Rate % (YoY)',
        yAxisTitle: 'Brand',
        data: [
          {
            legend: 'Depletion Growth Rate % (YoY)',
            values: [
              { x: 'Ridgeline Bourbon', y: 8.4 },
              { x: 'Apex Grill', y: 6.1 },
              { x: 'Coastline Tacos', y: 3.2 },
              { x: 'FreshMart', y: 1.9 },
              { x: 'Sierra Gold Tequila', y: -1.2 },
              { x: 'Summit Vodka', y: -3.4 },
            ],
          },
        ],
      };
    }
    case 'table': {
      const rows = c.expectedEntities.flatMap((brand) =>
        ['Midwest', 'Southeast'].map((region) => `${brand} — ${region}`),
      );
      return {
        type: 'table',
        title: 'Depletion Stats by Region',
        xAxisTitle: 'Brand / Region',
        yAxisTitle: 'Depletion Stats',
        data: [
          { legend: 'Depletions YoY %', values: rows.map((label, i) => ({ x: label, y: 2 + i * 0.5 })) },
          { legend: 'Sell-Through YoY %', values: rows.map((label, i) => ({ x: label, y: 1 + i * 0.3 })) },
          { legend: 'Inventory (weeks on hand)', values: rows.map((label, i) => ({ x: label, y: 7 + i * 0.2 })) },
        ],
      };
    }
    case 'gauge': {
      return {
        type: 'gauge',
        title: `${c.expectedEntities[0]} Inventory Health — Midwest`,
        data: [
          {
            legend: 'Inventory Health',
            values: [{ x: `${c.expectedEntities[0]} — Midwest`, y: 75 }],
          },
        ],
      };
    }
    default:
      throw new Error(`Unhandled chart visualization: ${type}`);
  }
}

const CHART_CASES = PROMPT_ACCEPTANCE_CASES.filter((c) => c.responseClass === 'chart');

// Chart-spec JSON leakage sentinels — a leak looks like the shape emitted by
// the model in issue #15 (canonical or Chart.js-style). None must ever survive
// to the rendered assistant DOM.
const JSON_LEAKAGE_PATTERNS = [
  /"chart_spec"\s*:/,
  /"type"\s*:\s*"(?:line|bar|groupedBar|horizontalBar|pie|donut|gauge|table)"/i,
  /"labels"\s*:\s*\[/,
  /"series"\s*:\s*\[\s*\{/,
];

// Per-case render ceiling: pure jsdom + real Recharts, no network, one card.
// Ships with a generous ceiling so the assertion catches order-of-magnitude
// regressions (e.g. an infinite render loop) without becoming a flake vector.
const RENDER_CEILING_MS = 1500;

describe('Chart prompt render acceptance (testids + no leakage + ceilings)', () => {
  it('covers every chart-class prompt from the manifest', () => {
    expect(CHART_CASES.length).toBe(9);
  });

  it.each(CHART_CASES.map((c) => [c.prompt, c] as const))(
    'renders "%s" with stable testids, no leakage, no fallback, under the perf ceiling',
    (_prompt, c) => {
      const spec = buildSpec(c);
      const t0 = performance.now();
      const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);
      const elapsed = performance.now() - t0;

      // ── Structural selectors (never Griffel classes) ─────────────────
      const card = container.querySelector('[data-testid="chart-card"]');
      expect(card, `${c.prompt} — chart-card testid`).not.toBeNull();
      expect(card!.getAttribute('data-chart-type')).toBe(c.expectedVisualization.toLowerCase());

      const title = container.querySelector('[data-testid="chart-title"]');
      expect(title, `${c.prompt} — chart-title testid`).not.toBeNull();
      expect(title!.textContent).toBe(spec.title);

      // Shape-specific structural anchor.
      switch (c.expectedVisualization) {
        case 'gauge':
          expect(container.querySelector('[data-testid="chart-gauge"]')).not.toBeNull();
          expect(container.querySelector('[data-testid="chart-gauge-svg"]')).not.toBeNull();
          break;
        case 'table':
          expect(container.querySelector('[data-testid="chart-table"]')).not.toBeNull();
          expect(container.querySelectorAll('[data-testid="chart-table"] tbody tr').length)
            .toBeGreaterThanOrEqual(c.expectedCategories);
          break;
        default:
          // Recharts SVG anchor — a real chart always emits a <svg class="recharts-surface">.
          expect(container.querySelector('svg.recharts-surface')).not.toBeNull();
      }

      // ── No JSON leakage ──────────────────────────────────────────────
      const rendered = container.textContent ?? '';
      for (const rx of JSON_LEAKAGE_PATTERNS) {
        expect(
          rendered,
          `${c.prompt} — rendered DOM must not leak chart-spec JSON (${rx})`,
        ).not.toMatch(rx);
      }

      // ── No fallback error text ───────────────────────────────────────
      expect(
        screen.queryByRole('note'),
        `${c.prompt} — 'Chart unavailable' fallback must NOT be rendered for a bindable spec`,
      ).toBeNull();
      expect(container.querySelector('[data-testid="chart-unavailable"]')).toBeNull();

      // ── Entity presence (title OR body) ──────────────────────────────
      for (const entity of c.expectedEntities) {
        const inTitle = spec.title.includes(entity);
        const inDom = rendered.includes(entity);
        expect(
          inTitle || inDom,
          `${c.prompt} — required entity '${entity}' must appear somewhere in the chart DOM`,
        ).toBe(true);
      }

      // ── Performance ceiling ──────────────────────────────────────────
      expect(
        elapsed,
        `${c.prompt} — rendered in ${elapsed.toFixed(0)}ms, ceiling ${RENDER_CEILING_MS}ms`,
      ).toBeLessThan(RENDER_CEILING_MS);
    },
  );

  it('surfaces the "chart-unavailable" fallback (never silent) when a spec has no bindable data', () => {
    // Regression guard for the acceptance-behavior promise: `noFallbackErrorText`
    // holds ONLY when the spec is bindable. This test verifies the opposite side
    // of the invariant — a truly unbindable spec MUST use the testid-anchored
    // fallback, not an empty card.
    const { container } = renderWithProvider(
      <ChartRenderer
        charts={[
          {
            type: 'bar',
            title: 'Unbindable regression fixture',
            data: [{ legend: 'x', values: [] }],
          },
        ]}
      />,
    );
    expect(container.querySelector('[data-testid="chart-unavailable"]')).not.toBeNull();
    expect(container.querySelector('[data-testid="chart-card"]')).toBeNull();
  });
});
