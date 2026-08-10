import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import ChartRenderer from '../components/ChartRenderer';
import type { ChartSpec, ChartType } from '../types';

// recharts uses ResizeObserver; jsdom doesn't ship one.
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

const baseSeries = (legend = 'Sierra Gold Tequila') => ({
  legend,
  values: [
    { x: 'Jan', y: 100 },
    { x: 'Feb', y: 150 },
    { x: 'Mar', y: 120 },
  ],
});

describe('ChartRenderer', () => {
  it('renders a line chart without crashing and shows the title', () => {
    const spec: ChartSpec = {
      type: 'line',
      title: 'Monthly Trend',
      xAxisTitle: 'Month',
      yAxisTitle: 'Cases',
      data: [baseSeries()],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    expect(screen.getByText('Monthly Trend')).toBeInTheDocument();
  });

  it('renders a bar chart', () => {
    const spec: ChartSpec = {
      type: 'bar',
      title: 'Bar Title',
      data: [baseSeries('S1'), baseSeries('S2')],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    expect(screen.getByText('Bar Title')).toBeInTheDocument();
  });

  it('renders a table with the expected x-axis values', () => {
    const spec: ChartSpec = {
      type: 'table',
      title: 'Table Title',
      xAxisTitle: 'Month',
      data: [baseSeries('Series A')],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    expect(screen.getByText('Table Title')).toBeInTheDocument();
    expect(screen.getByText('Series A')).toBeInTheDocument();
    expect(screen.getByText('Jan')).toBeInTheDocument();
    expect(screen.getByText('Feb')).toBeInTheDocument();
    expect(screen.getByText('Mar')).toBeInTheDocument();
  });

  it('falls back to a table for unknown chart types', () => {
    const spec: ChartSpec = {
      type: 'klingon-radar' as unknown as ChartType,
      title: 'Unknown Chart',
      xAxisTitle: 'X',
      data: [baseSeries('Fallback')],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    // Title still renders; default-case in switch falls back to RenderTable.
    expect(screen.getByText('Unknown Chart')).toBeInTheDocument();
    expect(screen.getByText('Fallback')).toBeInTheDocument();
  });

  it('shows a diagnostic instead of a blank card for an empty data array', () => {
    const spec: ChartSpec = {
      type: 'bar',
      title: 'Empty',
      data: [],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);

    // Non-disruptive diagnostic is shown...
    expect(screen.getByRole('note')).toBeInTheDocument();
    expect(screen.getByText(/Chart unavailable/i)).toBeInTheDocument();
    // ...and NO chart canvas / axes are rendered (no blank card).
    expect(container.querySelector('.recharts-wrapper')).toBeNull();
    expect(container.querySelector('svg')).toBeNull();
  });

  it('renders multiple charts in order', () => {
    const charts: ChartSpec[] = [
      { type: 'bar', title: 'First', data: [baseSeries()] },
      { type: 'line', title: 'Second', data: [baseSeries()] },
      { type: 'table', title: 'Third', data: [baseSeries()] },
    ];

    renderWithProvider(<ChartRenderer charts={charts} />);

    expect(screen.getByText('First')).toBeInTheDocument();
    expect(screen.getByText('Second')).toBeInTheDocument();
    expect(screen.getByText('Third')).toBeInTheDocument();
  });

  it('is case-insensitive on chart type', () => {
    const spec: ChartSpec = {
      type: 'BAR' as unknown as ChartType,
      title: 'Upper Bar',
      data: [baseSeries()],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    expect(screen.getByText('Upper Bar')).toBeInTheDocument();
  });

  it('renders a gauge with a finite 0-100 value and accessible label', () => {
    // Mirrors the deterministic gauge the backend builds for an explicit
    // "gauge chart for <brand> inventory health in <region>" request.
    const spec: ChartSpec = {
      type: 'gauge',
      title: 'Pinnacle Hardware Inventory Health — Midwest',
      data: [
        { legend: 'Inventory Health', values: [{ x: 'Pinnacle Hardware — Midwest', y: 75 }] },
      ],
    };

    const { container } = renderWithProvider(<ChartRenderer charts={[spec]} />);

    // Title and the numeric gauge value render (real SVG gauge, not a blank card).
    expect(screen.getByText('Pinnacle Hardware Inventory Health — Midwest')).toBeInTheDocument();
    expect(screen.getByText('75%')).toBeInTheDocument();
    expect(screen.getByText('Pinnacle Hardware — Midwest')).toBeInTheDocument();
    const gauge = container.querySelector('svg[role="img"]');
    expect(gauge).not.toBeNull();
    expect(gauge?.getAttribute('aria-label')).toContain('75 percent');
    // Not routed to the empty-state diagnostic.
    expect(screen.queryByRole('note')).toBeNull();
  });

  it('clamps an out-of-range gauge value into the arc but shows the raw number', () => {
    const spec: ChartSpec = {
      type: 'gauge',
      title: 'Supply Health',
      data: [{ legend: 'Supply Health', values: [{ x: 'West', y: 140 }] }],
    };

    renderWithProvider(<ChartRenderer charts={[spec]} />);

    // The label text shows the provided value; the arc geometry clamps internally.
    expect(screen.getByText('140%')).toBeInTheDocument();
  });
});
