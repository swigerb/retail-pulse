import { describe, it, expect, beforeAll, vi } from 'vitest';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { ChartSpec } from '../types';

// recharts' ResponsiveContainer measures 0x0 under jsdom, so it renders no inner
// marks. Replace it with a fixed-size wrapper that forwards an explicit
// width/height to the chart child so real bars/lines/legends are produced and we
// can assert on actual DOM marks (issue #32 regression: prove the comparison
// chart contains BOTH brand legends and real marks, not just a chart object).
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

// Exact production shape from the P0 report: two brands compared across regions.
const comparisonSpec = (type: ChartSpec['type']): ChartSpec => ({
  type,
  title: 'Coastline Tacos vs Apex Grill: Weekly Depletion Trend Across All Regions',
  data: [
    {
      legend: 'Coastline Tacos',
      values: [
        { x: 'West', y: 1200 },
        { x: 'Central', y: 980 },
        { x: 'East', y: 1450 },
      ],
    },
    {
      legend: 'Apex Grill',
      values: [
        { x: 'West', y: 1100 },
        { x: 'Central', y: 1320 },
        { x: 'East', y: 890 },
      ],
    },
  ],
});

describe('ChartRenderer two-brand regional comparison (issue #32)', () => {
  it('renders a grouped bar comparison with both brand legends and real bar marks', () => {
    const { container } = renderWithProvider(<ChartRenderer charts={[comparisonSpec('groupedBar' as ChartSpec['type'])]} />);

    // Two bar series groups, one per brand.
    const barSeries = container.querySelectorAll('.recharts-bar');
    expect(barSeries.length).toBe(2);

    // Actual rendered bar marks (3 regions x 2 brands = 6 rectangles).
    const barRects = container.querySelectorAll('.recharts-bar-rectangle');
    expect(barRects.length).toBeGreaterThanOrEqual(6);

    // Both brand legends present.
    const legendTexts = Array.from(container.querySelectorAll('.recharts-legend-item-text')).map((n) => n.textContent);
    expect(legendTexts).toContain('Coastline Tacos');
    expect(legendTexts).toContain('Apex Grill');

    // Regional x categories rendered on the axis.
    expect(screen.getAllByText('West').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Central').length).toBeGreaterThan(0);
    expect(screen.getAllByText('East').length).toBeGreaterThan(0);
  });

  it('renders a line comparison with both brand legends and real line marks', () => {
    const { container } = renderWithProvider(<ChartRenderer charts={[comparisonSpec('line')]} />);

    const lineSeries = container.querySelectorAll('.recharts-line');
    expect(lineSeries.length).toBe(2);

    const lineCurves = container.querySelectorAll('.recharts-line-curve');
    expect(lineCurves.length).toBe(2);

    const legendTexts = Array.from(container.querySelectorAll('.recharts-legend-item-text')).map((n) => n.textContent);
    expect(legendTexts).toContain('Coastline Tacos');
    expect(legendTexts).toContain('Apex Grill');
  });

  it('surfaces a diagnostic (no blank chart) when a comparison series has no finite points', () => {
    const spec: ChartSpec = {
      type: 'groupedBar' as ChartSpec['type'],
      title: 'Coastline Tacos vs Apex Grill: Weekly Depletion Trend Across All Regions',
      data: [
        { legend: 'Coastline Tacos', values: [] },
        { legend: 'Apex Grill', values: [] },
      ],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);

    expect(screen.getByRole('note')).toBeInTheDocument();
    expect(screen.getByText(/Chart unavailable/i)).toBeInTheDocument();
    // No chart canvas and no bar marks — the blank card is never emitted.
    expect(container.querySelector('svg')).toBeNull();
    expect(container.querySelector('.recharts-bar-rectangle')).toBeNull();
  });

  it('drops non-finite points but still renders marks for the finite ones', () => {
    const spec: ChartSpec = {
      type: 'groupedBar' as ChartSpec['type'],
      title: 'Coastline Tacos vs Apex Grill: Weekly Depletion Trend Across All Regions',
      data: [
        {
          legend: 'Coastline Tacos',
          values: [
            { x: 'West', y: 1200 },
            { x: 'Central', y: Number.NaN as unknown as number },
          ],
        },
        {
          legend: 'Apex Grill',
          values: [
            { x: 'West', y: 1100 },
            { x: 'Central', y: 1320 },
          ],
        },
      ],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);

    // Chart still renders (at least one finite point present).
    expect(container.querySelectorAll('.recharts-bar-rectangle').length).toBeGreaterThan(0);
    const legendTexts = Array.from(container.querySelectorAll('.recharts-legend-item-text')).map((n) => n.textContent);
    expect(legendTexts).toContain('Coastline Tacos');
    expect(legendTexts).toContain('Apex Grill');
  });
});
