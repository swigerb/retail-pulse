import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import PromoRecommendation from '../components/promo/PromoRecommendation';
import type { PromoEvaluation } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const recommendedEval: PromoEvaluation = {
  recommendation: 'recommended',
  roi: 3.2,
  roiLower: 2.1,
  roiUpper: 4.5,
  reasoning: 'Strong historical performance.',
  timingAssessment: 'Good timing.',
  conflicts: [],
  seasonalityFit: 'Peak season',
  risks: [
    { type: 'Competition', detail: 'Rival promo active', severity: 'medium' },
    { type: 'Supply Chain', detail: 'Lead times extended', severity: 'high' },
    { type: 'Demand Shift', detail: 'Slight decline expected', severity: 'low' },
  ],
  similarCampaigns: 14,
  breakEvenDays: 21,
  historicalAvgRoi: 2.8,
};

const cautiousEval: PromoEvaluation = {
  ...recommendedEval,
  recommendation: 'cautious',
  roi: 1.5,
  roiLower: 0.8,
  roiUpper: 2.2,
  conflicts: ['Overlaps with Summer Sale', 'Competes with Brand X campaign'],
};

const notRecommendedEval: PromoEvaluation = {
  ...recommendedEval,
  recommendation: 'not_recommended',
  roi: 0.6,
  roiLower: 0.3,
  roiUpper: 0.9,
  breakEvenDays: null,
};

const insufficientEval: PromoEvaluation = {
  ...recommendedEval,
  recommendation: 'insufficient_history',
  roi: null,
  roiLower: null,
  roiUpper: null,
  similarCampaigns: 0,
  breakEvenDays: null,
  historicalAvgRoi: null,
  insufficientHistory: true,
  reasoning: 'Not enough comparable campaign history to model ROI for these inputs.',
};

describe('PromoRecommendation', () => {
  it('renders recommended state with green badge', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    expect(screen.getByTestId('promo-recommendation')).toBeInTheDocument();
    expect(screen.getByTestId('recommendation-badge')).toHaveTextContent('Recommended');
  });

  it('renders cautious state with conflicts', () => {
    render(wrap(<PromoRecommendation evaluation={cautiousEval} budget={25000} />));
    expect(screen.getByTestId('recommendation-badge')).toHaveTextContent('Cautious');
    expect(screen.getByText(/Overlaps with Summer Sale/)).toBeInTheDocument();
    expect(screen.getByText(/Competes with Brand X/)).toBeInTheDocument();
  });

  it('renders not recommended state', () => {
    render(wrap(<PromoRecommendation evaluation={notRecommendedEval} budget={25000} />));
    expect(screen.getByTestId('recommendation-badge')).toHaveTextContent('Not Recommended');
    expect(screen.getByTestId('roi-value')).toHaveTextContent('0.60x ROI');
  });

  it('shows that sub-breakeven campaigns do not break even', () => {
    render(wrap(<PromoRecommendation evaluation={notRecommendedEval} budget={25000} />));
    expect(screen.getByText(/Break-even: Does not break even/)).toBeInTheDocument();
  });

  it('displays ROI with confidence range', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    expect(screen.getByTestId('roi-value')).toHaveTextContent('3.2x ROI');
    expect(screen.getByTestId('roi-range')).toHaveTextContent('(2.1x to 4.5x)');
  });

  it('renders insufficient history without zero ROI placeholders', () => {
    render(wrap(<PromoRecommendation evaluation={insufficientEval} budget={25000} />));
    expect(screen.getByTestId('recommendation-badge')).toHaveTextContent('Not Enough History');
    expect(screen.getByTestId('roi-value')).toHaveTextContent('Not enough history');
    expect(screen.queryByTestId('roi-range')).not.toBeInTheDocument();
    expect(screen.queryByText(/Break-even:/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Hist\. Avg:/)).not.toBeInTheDocument();
    expect(screen.getByTestId('credibility-note')).toHaveTextContent('No comparable campaigns found');
  });

  it('displays timing assessment details', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    expect(screen.getByText(/Peak season/)).toBeInTheDocument();
    expect(screen.getByText(/Break-even: 21 days/)).toBeInTheDocument();
    expect(screen.getByText(/Hist\. Avg: 2\.8x/)).toBeInTheDocument();
  });

  it('renders risk cards sorted by severity (high first)', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    const riskCards = screen.getAllByTestId(/promo-risk-/);
    expect(riskCards.length).toBe(3);
    // High severity first
    expect(riskCards[0]).toHaveAttribute('data-testid', 'promo-risk-high');
    expect(riskCards[1]).toHaveAttribute('data-testid', 'promo-risk-medium');
    expect(riskCards[2]).toHaveAttribute('data-testid', 'promo-risk-low');
  });

  it('expands risk detail on click', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    const firstRisk = screen.getAllByTestId(/promo-risk-/)[0];
    expect(screen.queryByText('Lead times extended')).not.toBeInTheDocument();
    fireEvent.click(firstRisk);
    expect(screen.getByText('Lead times extended')).toBeInTheDocument();
  });

  it('shows credibility note with campaign count', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    expect(screen.getByTestId('credibility-note')).toHaveTextContent('Based on 14 similar campaigns');
  });

  it('shows submit for approval button when budget >= 50k', () => {
    const onSubmit = vi.fn();
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={75000} onSubmitForApproval={onSubmit} />));
    expect(screen.getByTestId('submit-approval-button')).toBeInTheDocument();
  });

  it('does NOT show submit for approval button when budget < 50k', () => {
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={25000} />));
    expect(screen.queryByTestId('submit-approval-button')).not.toBeInTheDocument();
  });

  it('calls onSubmitForApproval when approval button clicked', () => {
    const onSubmit = vi.fn();
    render(wrap(<PromoRecommendation evaluation={recommendedEval} budget={75000} onSubmitForApproval={onSubmit} />));
    fireEvent.click(screen.getByTestId('submit-approval-button'));
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });
});
