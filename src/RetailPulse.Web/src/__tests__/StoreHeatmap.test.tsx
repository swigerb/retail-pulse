import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { StoreHeatmap } from '../components/stores/StoreHeatmap';
import type { StorePerformance } from '../types';

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const mockStores: StorePerformance[] = [
  {
    storeId: 's1',
    storeName: 'Downtown Flagship',
    region: 'West',
    revenue: 120_000,
    target: 100_000,
    performanceIndex: 120,
    issues: ['Low foot traffic on weekdays'],
    recommendations: ['Run weekday promo'],
  },
  {
    storeId: 's2',
    storeName: 'Mall Outlet',
    region: 'West',
    revenue: 85_000,
    target: 100_000,
    performanceIndex: 85,
    issues: [],
    recommendations: [],
  },
  {
    storeId: 's3',
    storeName: 'Airport Kiosk',
    region: 'East',
    revenue: 50_000,
    target: 100_000,
    performanceIndex: 50,
    issues: ['Staff shortage', 'Supply delay'],
    recommendations: ['Hire temp staff'],
  },
];

describe('StoreHeatmap', () => {
  it('renders store abbreviations from data', () => {
    renderWithProvider(<StoreHeatmap stores={mockStores} />);

    // "Downtown Flagship" → abbreviate → "DF"
    expect(screen.getByText('DF')).toBeInTheDocument();
    // "Mall Outlet" → "MO"
    expect(screen.getByText('MO')).toBeInTheDocument();
    // "Airport Kiosk" → "AK"
    expect(screen.getByText('AK')).toBeInTheDocument();
  });

  it('groups stores by region with visible region headers', () => {
    renderWithProvider(<StoreHeatmap stores={mockStores} />);

    expect(screen.getByText('West')).toBeInTheDocument();
    expect(screen.getByText('East')).toBeInTheDocument();
  });

  it('applies correct aria-labels reflecting performance index', () => {
    renderWithProvider(<StoreHeatmap stores={mockStores} />);

    // Green store (revenue/target >= 100%)
    expect(
      screen.getByLabelText('Downtown Flagship - performance 120'),
    ).toBeInTheDocument();

    // Yellow store (revenue/target 80-100%)
    expect(
      screen.getByLabelText('Mall Outlet - performance 85'),
    ).toBeInTheDocument();

    // Red store (revenue/target < 80%)
    expect(
      screen.getByLabelText('Airport Kiosk - performance 50'),
    ).toBeInTheDocument();
  });

  it('fires onStoreClick with storeId when a cell is clicked', () => {
    const onClick = vi.fn();
    renderWithProvider(<StoreHeatmap stores={mockStores} onStoreClick={onClick} />);

    const cells = screen.getAllByTestId('heatmap-cell');
    fireEvent.click(cells[0]);

    expect(onClick).toHaveBeenCalledTimes(1);
    // Stores are grouped by region sorted alphabetically: East first, then West
    // East has s3, West has s1 then s2
    expect(onClick).toHaveBeenCalledWith('s3');
  });

  it('shows empty state when no stores provided', () => {
    renderWithProvider(<StoreHeatmap stores={[]} />);

    expect(screen.getByTestId('heatmap-empty')).toBeInTheDocument();
    expect(screen.getByText('No store data available')).toBeInTheDocument();
  });

  it('shows issues count in tooltip when hovering a cell', () => {
    renderWithProvider(<StoreHeatmap stores={mockStores} />);

    const cells = screen.getAllByTestId('heatmap-cell');
    // Hover over first cell (East / Airport Kiosk — has 2 issues)
    fireEvent.mouseEnter(cells[0]);

    const tooltip = screen.getByTestId('heatmap-tooltip');
    expect(tooltip).toHaveTextContent('Issues: 2');
  });
});
