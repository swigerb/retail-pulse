import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { PlanogramDiagram } from '../components/stores/PlanogramDiagram';
import type { PlanogramLayout } from '../types';

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const mockLayout: PlanogramLayout = {
  shelfCount: 3,
  eyeLevelShelves: [2],
  slots: [
    { shelfLevel: 1, position: 1, skuName: 'Tequila Gold', brand: 'Sierra', brandColor: '#f59e0b', facingWidth: 2 },
    { shelfLevel: 2, position: 1, skuName: 'Vodka Premium', brand: 'Arctic', brandColor: '#3b82f6', facingWidth: 1, predictedUplift: 12 },
    { shelfLevel: 2, position: 2, skuName: 'Gin Select', brand: 'Botanic', brandColor: '#22c55e', facingWidth: 1 },
    { shelfLevel: 3, position: 1, skuName: 'Rum Dark', brand: 'Havana', brandColor: '#a855f7', facingWidth: 3 },
  ],
};

const emptyLayout: PlanogramLayout = { shelfCount: 0, slots: [], eyeLevelShelves: [] };

describe('PlanogramDiagram', () => {
  it('renders shelf level labels', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    expect(screen.getByText('Shelf 1')).toBeInTheDocument();
    expect(screen.getByText('Shelf 2')).toBeInTheDocument();
    expect(screen.getByText('Shelf 3')).toBeInTheDocument();
  });

  it('renders SKU names on shelves', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    expect(screen.getByText('Tequila Gold')).toBeInTheDocument();
    expect(screen.getByText('Vodka Premium')).toBeInTheDocument();
    expect(screen.getByText('Gin Select')).toBeInTheDocument();
    expect(screen.getByText('Rum Dark')).toBeInTheDocument();
  });

  it('shows eye-level indicator text', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    expect(screen.getByText('👁 Eye Level')).toBeInTheDocument();
  });

  it('renders predicted uplift badges when present', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    const badges = screen.getAllByTestId('uplift-badge');
    expect(badges).toHaveLength(1);
    expect(badges[0]).toHaveTextContent('+12%');
  });

  it('shows Before and After labels in comparison mode', () => {
    renderWithProvider(
      <PlanogramDiagram before={mockLayout} after={mockLayout} comparisonMode />,
    );

    expect(screen.getByText('Before')).toBeInTheDocument();
    expect(screen.getByText('After')).toBeInTheDocument();
  });

  it('renders correct number of shelves', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    expect(screen.getByTestId('shelf-row-1')).toBeInTheDocument();
    expect(screen.getByTestId('shelf-row-2')).toBeInTheDocument();
    expect(screen.getByTestId('shelf-row-3')).toBeInTheDocument();
  });

  it('shows empty state when both layouts have zero shelves', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={emptyLayout} />);

    expect(screen.getByTestId('planogram-empty')).toBeInTheDocument();
    expect(screen.getByText('No planogram data available')).toBeInTheDocument();
  });

  it('renders correct number of slots', () => {
    renderWithProvider(<PlanogramDiagram before={emptyLayout} after={mockLayout} />);

    const slots = screen.getAllByTestId('planogram-slot');
    expect(slots).toHaveLength(4);
  });
});
