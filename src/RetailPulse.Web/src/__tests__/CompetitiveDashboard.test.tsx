import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { CompetitorPricing, MarketShareEntry, CompetitiveThreat } from '../types';

const mockPricing: CompetitorPricing[] = [
  {
    competitor: 'GrillMax',
    sku: 'Premium Grill',
    category: 'Grills',
    currentPrice: 299.99,
    previousPrice: 349.99,
    changePercent: -14.3,
    priceHistory: [{ month: 'Jan', price: 349 }, { month: 'Feb', price: 329 }, { month: 'Mar', price: 299 }],
  },
];

const mockMarketShare: MarketShareEntry[] = [
  { quarter: 'Q1 2026', brand: 'Apex Grill', share: 32.5, isOurBrand: true },
  { quarter: 'Q1 2026', brand: 'GrillMax', share: 24.1, isOurBrand: false },
];

const mockThreats: CompetitiveThreat[] = [
  {
    id: 'threat-1',
    title: 'GrillMax launching aggressive price cut',
    severity: 'high',
    recommendation: 'MATCH',
    description: 'GrillMax has dropped prices 14% across premium grills.',
    reasoning: 'Historical data shows matching within 2 weeks preserves share.',
    historicalContext: 'GrillMax tried this in Q3 2025 and gained 3% share.',
    competitor: 'GrillMax',
    category: 'Grills',
    detectedAt: '2026-05-13T10:00:00Z',
  },
  {
    id: 'threat-2',
    title: 'New competitor entering sauces market',
    severity: 'medium',
    recommendation: 'DIFFERENTIATE',
    description: 'FlavorCo is launching a premium sauce line in the Southeast.',
    reasoning: 'Their target demographic overlaps with ours.',
    historicalContext: 'New entrants typically capture 5-8% in the first year.',
    competitor: 'FlavorCo',
    category: 'Sauces',
    detectedAt: '2026-05-12T14:00:00Z',
  },
  {
    id: 'threat-3',
    title: 'Seasonal promo overlap detected',
    severity: 'low',
    recommendation: 'IGNORE',
    description: 'Minor promo overlap in accessories category.',
    reasoning: 'Impact is minimal based on historical patterns.',
    historicalContext: 'Similar overlaps in the past had <1% share impact.',
    competitor: 'GearPro',
    category: 'Accessories',
    detectedAt: '2026-05-11T09:00:00Z',
  },
];

const mockFetchPricing = vi.fn().mockResolvedValue(mockPricing);
const mockFetchMarketShare = vi.fn().mockResolvedValue(mockMarketShare);
const mockFetchThreats = vi.fn().mockResolvedValue(mockThreats);
const mockFetchProfile = vi.fn().mockResolvedValue({
  name: 'GrillMax', categories: ['Grills'], regions: ['Southeast'],
  recentMoves: [], pricingHistory: [], marketShare: 24.1,
});
const mockGeneratePlan = vi.fn().mockResolvedValue({ plan: 'Match pricing within 1 week.' });

vi.mock('../services/competitiveApi', () => ({
  fetchCompetitorPricing: (...args: unknown[]) => mockFetchPricing(...args),
  fetchMarketShare: (...args: unknown[]) => mockFetchMarketShare(...args),
  fetchThreats: (...args: unknown[]) => mockFetchThreats(...args),
  fetchCompetitorProfile: (...args: unknown[]) => mockFetchProfile(...args),
  generateResponsePlan: (...args: unknown[]) => mockGeneratePlan(...args),
}));

import CompetitiveDashboard from '../components/competitive/CompetitiveDashboard';
import ThreatCards from '../components/competitive/ThreatCards';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('CompetitiveDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the competitive dashboard with title and filters', async () => {
    render(wrap(<CompetitiveDashboard />));
    expect(screen.getByTestId('competitive-dashboard')).toBeInTheDocument();
    expect(screen.getByText(/Competitive Intelligence/)).toBeInTheDocument();
    expect(screen.getByTestId('category-filter')).toBeInTheDocument();
    expect(screen.getByTestId('region-filter')).toBeInTheDocument();
  });

  it('loads data on mount and shows overview tab', async () => {
    render(wrap(<CompetitiveDashboard />));
    await waitFor(() => {
      expect(mockFetchPricing).toHaveBeenCalled();
      expect(mockFetchMarketShare).toHaveBeenCalled();
      expect(mockFetchThreats).toHaveBeenCalled();
    });
    expect(screen.getByTestId('overview-tab')).toBeInTheDocument();
  });

  it('switches to pricing tab when clicked', async () => {
    render(wrap(<CompetitiveDashboard />));
    await waitFor(() => expect(mockFetchPricing).toHaveBeenCalled());
    fireEvent.click(screen.getAllByText('💰 Pricing')[0]);
    expect(screen.getByTestId('pricing-tab')).toBeInTheDocument();
  });

  it('switches to threats tab when clicked', async () => {
    render(wrap(<CompetitiveDashboard />));
    await waitFor(() => expect(mockFetchThreats).toHaveBeenCalled());
    fireEvent.click(screen.getAllByText('🚨 Threats')[0]);
    expect(screen.getByTestId('threats-tab')).toBeInTheDocument();
  });

  it('shows error state when API fails', async () => {
    mockFetchPricing.mockRejectedValueOnce(new Error('Network error'));
    mockFetchMarketShare.mockRejectedValueOnce(new Error('Network error'));
    mockFetchThreats.mockRejectedValueOnce(new Error('Network error'));
    render(wrap(<CompetitiveDashboard />));
    await waitFor(() => {
      expect(screen.getByTestId('error-message')).toBeInTheDocument();
    });
  });
});

describe('ThreatCards', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders threat cards sorted by severity', () => {
    render(wrap(<ThreatCards threats={mockThreats} />));
    const cards = screen.getAllByTestId('threat-card');
    expect(cards).toHaveLength(3);
    // First card should be high severity
    expect(cards[0]).toHaveTextContent('GrillMax launching aggressive price cut');
  });

  it('shows severity and recommendation badges', () => {
    render(wrap(<ThreatCards threats={[mockThreats[0]]} />));
    expect(screen.getByTestId('severity-badge')).toHaveTextContent('High');
    expect(screen.getByTestId('recommendation-badge')).toHaveTextContent('MATCH');
  });

  it('expands to show reasoning and historical context', () => {
    render(wrap(<ThreatCards threats={[mockThreats[0]]} />));
    expect(screen.queryByTestId('threat-details')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('View Details'));
    expect(screen.getByTestId('threat-details')).toBeInTheDocument();
    expect(screen.getByText(/Historical data shows matching/)).toBeInTheDocument();
  });

  it('generates response plan when button clicked', async () => {
    render(wrap(<ThreatCards threats={[mockThreats[0]]} />));
    fireEvent.click(screen.getByText('📋 Generate Response Plan'));
    await waitFor(() => {
      expect(mockGeneratePlan).toHaveBeenCalledWith('threat-1');
    });
  });

  it('shows empty state when no threats', () => {
    render(wrap(<ThreatCards threats={[]} />));
    expect(screen.getByTestId('threats-empty')).toBeInTheDocument();
  });

  it('renders competitor and category meta tags', () => {
    render(wrap(<ThreatCards threats={[mockThreats[0]]} />));
    expect(screen.getByText('⚔️ GrillMax')).toBeInTheDocument();
    expect(screen.getByText('🏷️ Grills')).toBeInTheDocument();
  });
});
