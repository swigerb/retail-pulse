import { describe, it, expect } from 'vitest';
import type { ChartSpec } from '../types';
import { chartHasVisibleMagnitude, chartIsRenderable } from '../components/chartBindability';

/**
 * Unit contract for the sibling helper introduced in #74. `chartIsRenderable`
 * intentionally treats 0 as a legal finite datapoint. `chartHasVisibleMagnitude`
 * is the stricter guard used at the render boundary for chart types where a
 * zero payload would paint an empty shell (bars, lines, pie/donut).
 */

function bar(values: Array<{ x: string; y: number }>): ChartSpec {
  return {
    type: 'horizontalBar',
    title: 'Brands Ranked by Depletion Growth Rate (YoY)',
    xAxisTitle: 'Growth %',
    yAxisTitle: 'Brand',
    data: [{ legend: 'Growth %', values }],
  };
}

describe('chartHasVisibleMagnitude', () => {
  it('accepts a well-formed horizontalBar ranking with 6+ non-zero marks', () => {
    const spec = bar([
      { x: 'A', y: 8.4 },
      { x: 'B', y: 6.1 },
      { x: 'C', y: 4.7 },
      { x: 'D', y: 3.2 },
      { x: 'E', y: 2.1 },
      { x: 'F', y: 1.4 },
    ]);
    expect(chartIsRenderable(spec)).toBe(true);
    expect(chartHasVisibleMagnitude(spec)).toBe(true);
  });

  it('rejects an all-zero horizontalBar payload (chartIsRenderable would accept it)', () => {
    const spec = bar([
      { x: 'A', y: 0 },
      { x: 'B', y: 0 },
      { x: 'C', y: 0 },
      { x: 'D', y: 0 },
      { x: 'E', y: 0 },
      { x: 'F', y: 0 },
      { x: 'G', y: 0 },
    ]);
    expect(chartIsRenderable(spec)).toBe(true);
    expect(chartHasVisibleMagnitude(spec)).toBe(false);
  });

  it('rejects an underpopulated ranking (< 6 marks)', () => {
    const spec = bar([
      { x: 'A', y: 8.4 },
      { x: 'B', y: 6.1 },
      { x: 'C', y: 4.7 },
    ]);
    expect(chartHasVisibleMagnitude(spec)).toBe(false);
  });

  it('accepts a gauge with y=0 (magnitude-insensitive type)', () => {
    const gauge: ChartSpec = {
      type: 'gauge',
      title: 'Inventory Health',
      data: [{ legend: 'Health', values: [{ x: 'Store 42', y: 0 }] }],
    };
    expect(chartHasVisibleMagnitude(gauge)).toBe(true);
  });

  it('accepts a table with mixed zero and non-zero values', () => {
    const table: ChartSpec = {
      type: 'table',
      title: 'Depletion Stats',
      data: [
        { legend: 'YoY %', values: [{ x: 'A', y: 0 }, { x: 'B', y: 1 }] },
      ],
    };
    expect(chartHasVisibleMagnitude(table)).toBe(true);
  });

  it('rejects any spec where a series has no non-zero magnitude', () => {
    const twoSeries: ChartSpec = {
      type: 'bar',
      title: 'Two-brand comparison',
      data: [
        { legend: 'Brand A', values: [{ x: 'W', y: 1 }, { x: 'E', y: 2 }] },
        { legend: 'Brand B', values: [{ x: 'W', y: 0 }, { x: 'E', y: 0 }] },
      ],
    };
    expect(chartIsRenderable(twoSeries)).toBe(true);
    expect(chartHasVisibleMagnitude(twoSeries)).toBe(false);
  });

  it('rejects a null/undefined/untyped spec', () => {
    expect(chartHasVisibleMagnitude(null)).toBe(false);
    expect(chartHasVisibleMagnitude(undefined)).toBe(false);
  });
});
