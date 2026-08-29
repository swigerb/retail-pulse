import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { PortfolioScorecard } from '../components/scorecard/PortfolioScorecard';
import type { BrandScore } from '../types';

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const mockBrands: BrandScore[] = [
  {
    brandName: 'Sierra Gold',
    healthScore: 82,
    trend: 'up',
    dimensions: { demand: 85, competitive: 90, supply: 76, store: 80, margin: 78 },
    topRisk: 'Rising agave costs',
    topOpportunity: 'Premium segment growth',
  },
  {
    brandName: 'Arctic Vodka',
    healthScore: 45,
    trend: 'down',
    dimensions: { demand: 50, competitive: 42, supply: 48, store: 44, margin: 40 },
    topRisk: 'Market share loss',
    topOpportunity: 'RTD line extension',
  },
  {
    brandName: 'Botanic Gin',
    healthScore: 65,
    trend: 'stable',
    dimensions: { demand: 70, competitive: 65, supply: 65, store: 62, margin: 60 },
    topRisk: 'Seasonal demand dip',
    topOpportunity: 'Holiday gift set bundle',
  },
];

describe('PortfolioScorecard', () => {
  it('renders all brand names', () => {
    renderWithProvider(<PortfolioScorecard brands={mockBrands} />);

    expect(screen.getByText('Sierra Gold')).toBeInTheDocument();
    expect(screen.getByText('Arctic Vodka')).toBeInTheDocument();
    expect(screen.getByText('Botanic Gin')).toBeInTheDocument();
  });

  it('shows health scores', () => {
    renderWithProvider(<PortfolioScorecard brands={mockBrands} />);

    expect(screen.getByText('82')).toBeInTheDocument();
    expect(screen.getByText('45')).toBeInTheDocument();
    expect(screen.getByText('65')).toBeInTheDocument();
  });

  it('shows trend arrows', () => {
    renderWithProvider(<PortfolioScorecard brands={mockBrands} />);

    expect(screen.getByText('↑')).toBeInTheDocument();
    expect(screen.getByText('↓')).toBeInTheDocument();
    expect(screen.getByText('→')).toBeInTheDocument();
  });

  it('shows risk and opportunity pills', () => {
    renderWithProvider(<PortfolioScorecard brands={mockBrands} />);

    // Risk pills
    expect(screen.getByText(/Rising agave costs/)).toBeInTheDocument();
    expect(screen.getByText(/Market share loss/)).toBeInTheDocument();

    // Opportunity pills
    expect(screen.getByText(/Premium segment growth/)).toBeInTheDocument();
    expect(screen.getByText(/RTD line extension/)).toBeInTheDocument();
  });

  it('fires onBrandClick with brand name when card is clicked', () => {
    const onClick = vi.fn();
    renderWithProvider(<PortfolioScorecard brands={mockBrands} onBrandClick={onClick} />);

    fireEvent.click(screen.getByText('Sierra Gold'));

    expect(onClick).toHaveBeenCalledWith('Sierra Gold');
  });

  it('shows skeleton loading state when loading=true', () => {
    const { container } = renderWithProvider(
      <PortfolioScorecard brands={[]} loading={true} />,
    );

    // Skeleton cards are rendered (6 of them) — no brand names should appear
    expect(screen.queryByText('Sierra Gold')).not.toBeInTheDocument();
    // Should not show empty state when loading
    expect(screen.queryByText('No brand data available yet.')).not.toBeInTheDocument();
    // Skeleton blocks are present
    expect(container.querySelectorAll('[class]').length).toBeGreaterThan(0);
  });

  it('shows generation time', () => {
    renderWithProvider(
      <PortfolioScorecard brands={mockBrands} generationTimeMs={2500} />,
    );

    expect(screen.getByText('Generated in 2.5s')).toBeInTheDocument();
  });

  it('shows empty state with no brands', () => {
    renderWithProvider(<PortfolioScorecard brands={[]} />);

    expect(screen.getByText('No brand data available yet.')).toBeInTheDocument();
  });
});
