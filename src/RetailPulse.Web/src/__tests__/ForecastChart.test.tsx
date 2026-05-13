import { describe, it, expect, beforeAll } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { ForecastChart } from '../components/forecast';
import { DemandRiskCards } from '../components/forecast';
import type { ForecastData } from '../types';

// Recharts uses ResizeObserver; jsdom doesn't ship one.
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

const mockForecastData: ForecastData = {
  brand: 'Sierra Gold Tequila',
  region: 'Southwest',
  period: { start: '2026-01-01', end: '2026-06-30' },
  historical: [
    { date: '2026-01-01', actual: 1200 },
    { date: '2026-02-01', actual: 1350 },
    { date: '2026-03-01', actual: 1100 },
  ],
  predicted: [
    { date: '2026-04-01', value: 1400, lower: 1200, upper: 1600 },
    { date: '2026-05-01', value: 1550, lower: 1300, upper: 1800 },
    { date: '2026-06-01', value: 1700, lower: 1400, upper: 2000 },
  ],
  seasonality: [
    { factor: 'Summer', impact: '+15%', period: 'May-Aug', startDate: '2026-05-01', endDate: '2026-06-01' },
  ],
  risks: [
    { type: 'Supply Shortage', severity: 'high', description: 'Agave supply chain disruption expected in Q2', affectedPeriod: 'Apr-Jun 2026' },
    { type: 'Seasonal Dip', severity: 'low', description: 'Minor slowdown expected in early spring', affectedPeriod: 'Mar 2026' },
    { type: 'Price Sensitivity', severity: 'medium', description: 'Competitor pricing may impact demand', affectedPeriod: 'May 2026' },
  ],
};

describe('ForecastChart', () => {
  it('renders without crashing and shows the brand title', () => {
    renderWithProvider(<ForecastChart data={mockForecastData} />);
    expect(screen.getByTestId('forecast-chart')).toBeInTheDocument();
    expect(screen.getByText(/Sierra Gold Tequila Demand Forecast/)).toBeInTheDocument();
  });

  it('renders the forecast summary KPI strip', () => {
    renderWithProvider(<ForecastChart data={mockForecastData} />);
    expect(screen.getByTestId('forecast-summary')).toBeInTheDocument();
    expect(screen.getByText('Current Avg')).toBeInTheDocument();
    expect(screen.getByText('Forecast Avg')).toBeInTheDocument();
    expect(screen.getByText('Trend')).toBeInTheDocument();
    expect(screen.getByText('Top Seasonal Factor')).toBeInTheDocument();
  });

  it('shows the region in the title', () => {
    renderWithProvider(<ForecastChart data={mockForecastData} />);
    expect(screen.getByText(/Southwest/)).toBeInTheDocument();
  });

  it('renders the risk cards section', () => {
    renderWithProvider(<ForecastChart data={mockForecastData} />);
    expect(screen.getByTestId('demand-risk-cards')).toBeInTheDocument();
    expect(screen.getByText('Identified Risks')).toBeInTheDocument();
  });

  it('handles empty historical data', () => {
    const emptyData: ForecastData = {
      ...mockForecastData,
      historical: [],
    };
    expect(() => renderWithProvider(<ForecastChart data={emptyData} />)).not.toThrow();
  });

  it('handles empty predicted data', () => {
    const emptyData: ForecastData = {
      ...mockForecastData,
      predicted: [],
    };
    expect(() => renderWithProvider(<ForecastChart data={emptyData} />)).not.toThrow();
  });
});

describe('DemandRiskCards', () => {
  it('renders all risk cards', () => {
    renderWithProvider(<DemandRiskCards risks={mockForecastData.risks} />);
    expect(screen.getByText('Supply Shortage')).toBeInTheDocument();
    expect(screen.getByText('Price Sensitivity')).toBeInTheDocument();
    expect(screen.getByText('Seasonal Dip')).toBeInTheDocument();
  });

  it('sorts risks by severity (high first)', () => {
    renderWithProvider(<DemandRiskCards risks={mockForecastData.risks} />);
    const cards = screen.getAllByTestId(/^risk-card-/);
    expect(cards[0]).toHaveAttribute('data-testid', 'risk-card-high');
    expect(cards[1]).toHaveAttribute('data-testid', 'risk-card-medium');
    expect(cards[2]).toHaveAttribute('data-testid', 'risk-card-low');
  });

  it('expands detail on click', () => {
    renderWithProvider(<DemandRiskCards risks={mockForecastData.risks} />);
    // Detail should NOT be visible initially
    expect(screen.queryByText(/Agave supply chain disruption/)).not.toBeInTheDocument();
    // Click to expand
    fireEvent.click(screen.getByText('Supply Shortage').closest('[role="button"]')!);
    expect(screen.getByText(/Agave supply chain disruption/)).toBeInTheDocument();
  });

  it('shows empty state when no risks', () => {
    renderWithProvider(<DemandRiskCards risks={[]} />);
    expect(screen.getByText('No risks identified')).toBeInTheDocument();
  });

  it('displays affected period for each risk', () => {
    renderWithProvider(<DemandRiskCards risks={mockForecastData.risks} />);
    expect(screen.getByText('Apr-Jun 2026')).toBeInTheDocument();
    expect(screen.getByText('Mar 2026')).toBeInTheDocument();
    expect(screen.getByText('May 2026')).toBeInTheDocument();
  });
});
