import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import type { MarginWaterfallStep } from '../types';

// Mock recharts so jsdom doesn't need to render SVG
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children }: { children: React.ReactNode }) => <div data-testid="responsive-container">{children}</div>,
  BarChart: ({ children }: { children: React.ReactNode }) => <div data-testid="bar-chart">{children}</div>,
  Bar: () => <div data-testid="bar" />,
  XAxis: () => <div data-testid="x-axis" />,
  YAxis: () => <div data-testid="y-axis" />,
  CartesianGrid: () => <div data-testid="grid" />,
  Tooltip: () => <div data-testid="tooltip" />,
  Cell: () => <div data-testid="cell" />,
  ReferenceLine: () => <div data-testid="reference-line" />,
  Legend: () => <div data-testid="legend" />,
}));

// Import after mock
import { MarginWaterfall } from '../components/margin/MarginWaterfall';

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

const mockSteps: MarginWaterfallStep[] = [
  { label: 'Revenue', value: 500_000 },
  { label: 'COGS', value: -200_000 },
  { label: 'Gross Profit', value: 300_000, isSubtotal: true },
  { label: 'OpEx', value: -100_000 },
  { label: 'Net Profit', value: 200_000, isSubtotal: true },
];

const comparisonSteps: MarginWaterfallStep[] = [
  { label: 'Revenue', value: 450_000 },
  { label: 'COGS', value: -180_000 },
  { label: 'Gross Profit', value: 270_000, isSubtotal: true },
  { label: 'OpEx', value: -90_000 },
  { label: 'Net Profit', value: 180_000, isSubtotal: true },
];

describe('MarginWaterfall', () => {
  it('renders the chart container', () => {
    renderWithProvider(<MarginWaterfall steps={mockSteps} />);

    expect(screen.getByTestId('margin-waterfall')).toBeInTheDocument();
  });

  it('shows title when provided', () => {
    renderWithProvider(<MarginWaterfall steps={mockSteps} title="Q4 Margin Breakdown" />);

    expect(screen.getByText('Q4 Margin Breakdown')).toBeInTheDocument();
  });

  it('renders with comparison data without crashing', () => {
    renderWithProvider(
      <MarginWaterfall steps={mockSteps} comparisonSteps={comparisonSteps} title="Compare" />,
    );

    expect(screen.getByTestId('margin-waterfall')).toBeInTheDocument();
    expect(screen.getByText('Compare')).toBeInTheDocument();
  });

  it('shows empty state when no steps provided', () => {
    renderWithProvider(<MarginWaterfall steps={[]} />);

    expect(screen.getByText('No margin data available')).toBeInTheDocument();
  });

  it('contains bar chart elements', () => {
    renderWithProvider(<MarginWaterfall steps={mockSteps} />);

    expect(screen.getByTestId('responsive-container')).toBeInTheDocument();
    expect(screen.getByTestId('bar-chart')).toBeInTheDocument();
  });
});
