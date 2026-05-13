import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import PromoCalendar from '../components/promo/PromoCalendar';
import type { PromoCampaign } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const now = new Date();
const oneWeekAgo = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
const twoWeeksOut = new Date(now.getTime() + 14 * 24 * 60 * 60 * 1000);
const threeWeeksOut = new Date(now.getTime() + 21 * 24 * 60 * 60 * 1000);

const mockCampaigns: PromoCampaign[] = [
  {
    id: 'camp-1',
    name: 'Summer Sale',
    brand: 'Apex Grill',
    region: 'Northeast',
    promoType: 'Discount',
    budget: 30000,
    startDate: oneWeekAgo.toISOString().split('T')[0],
    endDate: twoWeeksOut.toISOString().split('T')[0],
    roi: 2.5,
    status: 'active',
  },
  {
    id: 'camp-2',
    name: 'Digital Push',
    brand: 'Coastal Catch',
    region: 'Southeast',
    promoType: 'Digital',
    budget: 15000,
    startDate: twoWeeksOut.toISOString().split('T')[0],
    endDate: threeWeeksOut.toISOString().split('T')[0],
    status: 'planned',
  },
];

// Overlapping proposed campaign for conflict detection
const overlappingProposed = {
  name: 'BOGO Blast',
  brand: 'Mountain Roast',
  region: 'Northeast',
  promoType: 'BOGO' as const,
  budget: 20000,
  startDate: now.toISOString().split('T')[0],
  endDate: twoWeeksOut.toISOString().split('T')[0],
};

describe('PromoCalendar', () => {
  it('renders calendar with title', () => {
    render(wrap(<PromoCalendar campaigns={mockCampaigns} />));
    expect(screen.getByTestId('promo-calendar')).toBeInTheDocument();
    expect(screen.getByText('📅 Campaign Calendar')).toBeInTheDocument();
  });

  it('renders campaign bars for each campaign', () => {
    render(wrap(<PromoCalendar campaigns={mockCampaigns} />));
    expect(screen.getByTestId('campaign-bar-camp-1')).toBeInTheDocument();
    expect(screen.getByTestId('campaign-bar-camp-2')).toBeInTheDocument();
  });

  it('renders proposed campaign as dashed bar', () => {
    render(wrap(<PromoCalendar campaigns={[]} proposedCampaign={overlappingProposed} />));
    expect(screen.getByTestId('campaign-bar-__proposed__')).toBeInTheDocument();
  });

  it('shows empty state when no campaigns', () => {
    render(wrap(<PromoCalendar campaigns={[]} />));
    expect(screen.getByText('No campaigns to display')).toBeInTheDocument();
  });

  it('renders legend with status colors', () => {
    render(wrap(<PromoCalendar campaigns={mockCampaigns} />));
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Planned')).toBeInTheDocument();
    expect(screen.getByText('Proposed')).toBeInTheDocument();
  });

  it('shows tooltip on hover', () => {
    render(wrap(<PromoCalendar campaigns={mockCampaigns} />));
    const bar = screen.getByTestId('campaign-bar-camp-1');
    fireEvent.mouseEnter(bar, { clientX: 100, clientY: 100 });
    expect(screen.getByTestId('calendar-tooltip')).toBeInTheDocument();
    expect(screen.getByText('Summer Sale')).toBeInTheDocument();
    expect(screen.getByText('Budget: $30,000')).toBeInTheDocument();
  });

  it('detects overlap between campaigns in same region', () => {
    // camp-1 is in Northeast, overlapping proposed is also in Northeast
    render(wrap(<PromoCalendar campaigns={mockCampaigns} proposedCampaign={overlappingProposed} />));
    // The proposed bar should be in the DOM
    expect(screen.getByTestId('campaign-bar-__proposed__')).toBeInTheDocument();
    // Hover the conflicting proposed campaign to see overlap indicator
    fireEvent.mouseEnter(screen.getByTestId('campaign-bar-__proposed__'), { clientX: 100, clientY: 100 });
    expect(screen.getByText(/Overlap detected/)).toBeInTheDocument();
  });

  it('groups campaigns by region', () => {
    render(wrap(<PromoCalendar campaigns={mockCampaigns} />));
    expect(screen.getByText('Northeast')).toBeInTheDocument();
    expect(screen.getByText('Southeast')).toBeInTheDocument();
  });
});
