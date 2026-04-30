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

  it('handles empty data array without crashing', () => {
    const spec: ChartSpec = {
      type: 'bar',
      title: 'Empty',
      data: [],
    };

    expect(() => renderWithProvider(<ChartRenderer charts={[spec]} />)).not.toThrow();
    expect(screen.getByText('Empty')).toBeInTheDocument();
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
});
