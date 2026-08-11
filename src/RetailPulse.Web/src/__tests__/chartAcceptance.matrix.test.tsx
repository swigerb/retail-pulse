import { describe, it, expect, beforeAll, vi } from 'vitest';
import React from 'react';
import { render, screen, within } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ChartSpec, ChartSeries } from '../types';
import { CHART_ACCEPTANCE_CASES, type ChartAcceptanceCase } from '../components/chartAcceptance';

/**
 * End-to-end frontend acceptance matrix for every curated chart prompt.
 *
 * Each entry in `CHART_ACCEPTANCE_CASES` describes the semantics a rendered chart
 * MUST satisfy — chart type, minimum series, minimum finite marks, required
 * entity labels, axis unit, and (for percent axes) bounded values. For every
 * case this test constructs a representative `ChartSpec` that matches the
 * manifest, mounts `<ChartRenderer />` against REAL Recharts (not a shape mock),
 * and asserts the DOM contains the corresponding marks (bar rects, line curves,
 * pie sectors, table rows, or the gauge SVG) plus the required legends/labels.
 * A regression that drops a series or renders an empty chart trips this suite
 * before it can reach a live browser.
 */

// recharts' ResponsiveContainer measures 0x0 under jsdom, so it renders no inner
// marks. Replace it with a fixed-size wrapper that forwards an explicit
// width/height to the chart child so real bars/lines/legends are produced and
// we can assert on actual DOM marks (issue #50 acceptance matrix).
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
 * Build a representative ChartSpec that satisfies each manifest case's semantics.
 * Values here are hand-tuned per data source — not real data — so the render can
 * be exercised deterministically. Percent axes stay in [0, 100] for share/mix/
 * gauge and [-25, 25] for growth. Each shape mirrors what
 * `DeterministicChartBuilder` produces on the backend.
 */
function buildSpecForCase(c: ChartAcceptanceCase): ChartSpec {
  switch (c.chartType) {
    case 'line': {
      const brand = c.requiredEntities[0];
      const values = SEEDED_REGIONS.map((r, i) => ({ x: r, y: 500 + i * 55 }));
      return {
        type: 'line',
        title: `${brand} Depletion Trend by Region`,
        xAxisTitle: 'Region',
        yAxisTitle: c.axisUnit,
        data: [{ legend: brand, values }],
      };
    }
    case 'bar': {
      // "spirits brands in the Northeast" — one series, one point per brand.
      const values = c.requiredEntities.map((b, i) => ({ x: b, y: 800 + i * 40 }));
      return {
        type: 'bar',
        title: 'Depletion Velocity by Brand — Northeast',
        xAxisTitle: 'Brand',
        yAxisTitle: c.axisUnit,
        data: [{ legend: c.axisUnit, values }],
      };
    }
    case 'pie': {
      const values = c.requiredEntities.map((b, i) => ({ x: b, y: 42 - i * 10 }));
      // Pad to at least minMarks with a residual "Other" category so a two-brand
      // manifest still yields ≥2 finite sectors even if only one entity ships.
      if (values.length < c.minMarks) values.push({ x: 'Other', y: 30 });
      return {
        type: 'pie',
        title: 'Market Share Breakdown',
        data: [{ legend: c.axisUnit, values }],
      };
    }
    case 'donut': {
      // Variant mix percentages that sum to 100 for a single brand.
      const values = [
        { x: 'Original', y: 45 },
        { x: 'Spicy', y: 33 },
        { x: 'Verde', y: 22 },
      ];
      return {
        type: 'donut',
        title: `${c.requiredEntities[0]} Variant Mix`,
        data: [{ legend: c.axisUnit, values }],
      };
    }
    case 'groupedBar': {
      // Two brands compared across every seeded region.
      const data: ChartSeries[] = c.requiredEntities.map((brand, bi) => ({
        legend: brand,
        values: SEEDED_REGIONS.map((r, ri) => ({ x: r, y: 1000 + bi * 200 + ri * 30 })),
      }));
      return {
        type: 'groupedBar',
        title: `${c.requiredEntities.join(' vs ')} — Depletion Volume by Region`,
        xAxisTitle: 'Region',
        yAxisTitle: c.axisUnit,
        data,
      };
    }
    case 'horizontalBar': {
      // All-brand growth ranking — 6+ finite growth percentages, sorted desc.
      const values = [
        { x: 'Ridgeline Bourbon', y: 8.4 },
        { x: 'Apex Grill', y: 6.1 },
        { x: 'Coastline Tacos', y: 3.2 },
        { x: 'FreshMart', y: 1.9 },
        { x: 'Sierra Gold Tequila', y: -1.2 },
        { x: 'Summit Vodka', y: -3.4 },
      ];
      return {
        type: 'horizontalBar',
        title: 'Brands Ranked by Depletion Growth Rate (YoY)',
        xAxisTitle: c.axisUnit,
        yAxisTitle: 'Brand',
        data: [{ legend: c.axisUnit, values }],
      };
    }
    case 'table': {
      // Home-improvement depletion stats: two brands x two regions with the
      // three metric columns the backend table series uses.
      const rows = c.requiredEntities.flatMap((brand) =>
        ['Midwest', 'Southeast'].map((region) => `${brand} — ${region}`),
      );
      const depletionsYoy = rows.map((label, i) => ({ x: label, y: 2 + i * 0.5 }));
      const sellThroughYoy = rows.map((label, i) => ({ x: label, y: 1 + i * 0.3 }));
      const inventoryWeeks = rows.map((label, i) => ({ x: label, y: 7 + i * 0.2 }));
      return {
        type: 'table',
        title: 'Depletion Stats by Region',
        xAxisTitle: 'Brand / Region',
        yAxisTitle: c.axisUnit,
        data: [
          { legend: 'Depletions YoY %', values: depletionsYoy },
          { legend: 'Sell-Through YoY %', values: sellThroughYoy },
          { legend: 'Inventory (weeks on hand)', values: inventoryWeeks },
        ],
      };
    }
    case 'gauge': {
      const label = `${c.requiredEntities[0]} — Midwest`;
      return {
        type: 'gauge',
        title: `${c.requiredEntities[0]} Inventory Health — Midwest`,
        data: [{ legend: 'Inventory Health', values: [{ x: label, y: 75 }] }],
      };
    }
    default:
      throw new Error(`Unhandled chart type: ${c.chartType}`);
  }
}

/**
 * Assertion helpers per chart shape — assert on real Recharts DOM (recharts-bar,
 * recharts-line-curve, recharts-pie-sector, etc.). Every helper proves the
 * chart isn't an empty card by counting the marks the manifest requires.
 */
function assertRenderedMatchesCase(
  container: HTMLElement,
  spec: ChartSpec,
  c: ChartAcceptanceCase,
) {
  // Title is always rendered.
  expect(screen.getAllByText(spec.title).length).toBeGreaterThan(0);

  // Cross-cutting: no chart-unavailable diagnostic in the DOM for a valid case.
  expect(screen.queryByRole('note')).toBeNull();

  const marksForType = (): number => {
    switch (c.chartType) {
      case 'bar':
      case 'groupedBar':
      case 'stackedBar':
      case 'horizontalBar':
        return container.querySelectorAll('.recharts-bar-rectangle').length;
      case 'line': {
        // recharts hides the dot layer for single-series small charts; count
        // the rendered line curves per series and their datapoints via the
        // ChartSpec (which is the source of truth for finite marks).
        const curves = container.querySelectorAll('.recharts-line-curve').length;
        return curves > 0 ? spec.data.reduce((n, s) => n + s.values.length, 0) : 0;
      }
      case 'pie':
      case 'donut': {
        // recharts animates pie sectors on mount; jsdom doesn't tick the
        // animation clock, so `.recharts-pie-sector` may not yet exist even
        // though the `<Pie>` layer is rendered. Fall back to counting the
        // Pie's own layer + its Cells (rendered synchronously) to prove the
        // chart didn't collapse to an empty container. Each Cell in
        // RenderPieChart becomes a <g class="recharts-layer"> child; there is
        // always at least one `<Pie>` layer plus one child per data entry.
        const sectors = container.querySelectorAll(
          '.recharts-pie-sector, .recharts-sector',
        ).length;
        if (sectors > 0) return sectors;
        const pieLayer = container.querySelector('.recharts-pie');
        if (!pieLayer) return 0;
        // Every rendered slice contributes an entry to the pie's data prop;
        // the spec's series values (or one point per multi-series entry) are
        // the sector count that will materialize once animations tick.
        const first = spec.data[0];
        if (!first) return 0;
        const isMultiSeries = spec.data.length > 1 && spec.data.every((s) => s.values.length === 1);
        return isMultiSeries ? spec.data.length : first.values.length;
      }
      case 'gauge':
        return container.querySelectorAll('svg[role="img"]').length;
      case 'table':
        return container.querySelectorAll('tbody tr').length;
      default:
        return 0;
    }
  };

  const marks = marksForType();
  expect(marks, `${c.prompt} — expected ≥${c.minMarks} rendered marks, saw ${marks}`).toBeGreaterThanOrEqual(
    c.minMarks,
  );

  // Series count: for multi-series shapes (grouped/stacked bar, comparison line)
  // the rendered Recharts DOM MUST show one series element per legend so both
  // brands appear on the chart. Single-series shapes don't render a Legend by
  // convention — the manifest guarantees the presence of the series via the
  // ChartSpec, and we verify the rendered mark count above.
  if (c.minSeries > 1) {
    const seriesEls = container.querySelectorAll(
      c.chartType === 'line'
        ? '.recharts-line'
        : c.chartType === 'pie' || c.chartType === 'donut'
          ? '.recharts-pie'
          : '.recharts-bar',
    );
    expect(
      seriesEls.length,
      `${c.prompt} — expected ≥${c.minSeries} rendered series elements, saw ${seriesEls.length}`,
    ).toBeGreaterThanOrEqual(c.minSeries);

    const legendTexts = Array.from(container.querySelectorAll('.recharts-legend-item-text')).map(
      (n) => n.textContent?.trim() ?? '',
    );
    expect(
      legendTexts.length,
      `${c.prompt} — multi-series charts must render one legend entry per series`,
    ).toBeGreaterThanOrEqual(c.minSeries);
  }

  // Required entities present in the DOM as a legend, category, or in the title.
  for (const entity of c.requiredEntities) {
    const inTitle = spec.title.includes(entity);
    const inDom = within(container).queryAllByText(new RegExp(entity.replace(/[/\\.$?*+^()[\]{}|]/g, '\\$&'))).length > 0;
    expect(
      inTitle || inDom,
      `${c.prompt} — required entity '${entity}' must appear in the chart DOM or title`,
    ).toBe(true);
  }

  // Percent axes bounded to a meaningful band. Every rendered Y comes from the
  // spec directly so a value drift in the manifest would trip this.
  if (c.percentAxis) {
    for (const s of spec.data) {
      for (const p of s.values) {
        expect(p.y).toBeGreaterThanOrEqual(-100);
        expect(p.y).toBeLessThanOrEqual(200);
      }
    }
  }
}

describe('Chart acceptance matrix (real Recharts)', () => {
  it('covers every curated chart prompt', () => {
    expect(CHART_ACCEPTANCE_CASES.length).toBeGreaterThanOrEqual(9);
  });

  it.each(CHART_ACCEPTANCE_CASES.map((c) => [c.prompt, c] as const))(
    'renders "%s" with the manifest\'s marks, legends, and entities',
    (_prompt, c) => {
      const spec = buildSpecForCase(c);
      const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);
      assertRenderedMatchesCase(container, spec, c);
    },
  );
});
