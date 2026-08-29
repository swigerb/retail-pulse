import { useState } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { BrandScoreCard } from '../components/scorecard/BrandScoreCard';
import { ExplanationPanel } from '../components/scorecard/ExplanationPanel';
import { buildScorecardExplanation } from '../scorecardModel';
import { toBrandScores } from '../services/operationsApi';
import type { BrandScore, ExplanationData, ScorecardDimensionKey } from '../types';

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const harvestTable: BrandScore = {
  brandName: 'Harvest Table',
  healthScore: 63,
  trend: 'stable',
  dimensions: { demand: 81, competitive: 52, supply: 35, store: 64, margin: 72 },
  dimensionDetails: {
    demand: {
      key: 'demand',
      label: 'Demand Momentum',
      shortLabel: 'Demand',
      score: 81,
      weight: 0.25,
      weightedScore: 20.25,
      assessment: 'POS velocity is accelerating in priority accounts.',
      agentKey: 'demand-forecasting',
    },
    competitive: {
      key: 'competitive',
      label: 'Competitive Position',
      shortLabel: 'Competitive',
      score: 52,
      weight: 0.20,
      weightedScore: 10.4,
      assessment: 'Competitor discounting is pressuring shelf share.',
      agentKey: 'competitive-intel',
    },
    supply: {
      key: 'supply',
      label: 'Supply Reliability',
      shortLabel: 'Supply',
      score: 35,
      weight: 0.20,
      weightedScore: 7,
      assessment: 'Fulfillment gaps and stockout risk are limiting replenishment.',
      agentKey: 'supply-chain',
    },
    store: {
      key: 'store',
      label: 'Store Execution',
      shortLabel: 'Store',
      score: 64,
      weight: 0.20,
      weightedScore: 12.8,
      assessment: 'Store execution is adequate but inconsistent across regions.',
      agentKey: 'store-ops',
    },
    margin: {
      key: 'margin',
      label: 'Margin Health',
      shortLabel: 'Margin',
      score: 72,
      weight: 0.15,
      weightedScore: 10.8,
      assessment: 'Gross margin is healthy despite commodity cost pressure.',
      agentKey: 'margin-analysis',
    },
  },
  topRisk: 'Improve Supply Reliability: Fulfillment gaps and stockout risk are limiting replenishment.',
  topOpportunity: 'Build on Demand Momentum strength (81)',
};

function Harness() {
  const [open, setOpen] = useState(false);
  const [explanation, setExplanation] = useState<ExplanationData | null>(null);

  const handleWhyClick = (dimension: ScorecardDimensionKey) => {
    setExplanation(buildScorecardExplanation(harvestTable, dimension));
    setOpen(true);
  };

  return (
    <>
      <BrandScoreCard brand={harvestTable} onWhyClick={handleWhyClick} />
      <ExplanationPanel explanation={explanation} open={open} onClose={() => setOpen(false)} />
    </>
  );
}

describe('scorecard explanations', () => {
  it('keeps assessment details from the scorecard API payload', () => {
    const [brand] = toBrandScores({
      totalDurationMs: 1200,
      brands: [{
        brand: 'Harvest Table',
        overallScore: 6.33,
        dimensions: {
          'Demand Momentum': {
            dimension: 'Demand Momentum',
            score: 8.1,
            weight: 0.25,
            weightedScore: 2.025,
            assessment: 'POS velocity is accelerating in priority accounts.',
            agentKey: 'demand-forecasting',
          },
          'Competitive Position': {
            dimension: 'Competitive Position',
            score: 5.2,
            weight: 0.20,
            weightedScore: 1.04,
            assessment: 'Competitor discounting is pressuring shelf share.',
            agentKey: 'competitive-intel',
          },
          'Supply Reliability': {
            dimension: 'Supply Reliability',
            score: 3.5,
            weight: 0.20,
            weightedScore: 0.7,
            assessment: 'Fulfillment gaps and stockout risk are limiting replenishment.',
            agentKey: 'supply-chain',
          },
          'Store Execution': {
            dimension: 'Store Execution',
            score: 6.4,
            weight: 0.20,
            weightedScore: 1.28,
            assessment: 'Store execution is adequate but inconsistent across regions.',
            agentKey: 'store-ops',
          },
          'Margin Health': {
            dimension: 'Margin Health',
            score: 7.2,
            weight: 0.15,
            weightedScore: 1.08,
            assessment: 'Gross margin is healthy despite commodity cost pressure.',
            agentKey: 'margin-analysis',
          },
        },
      }],
    });

    expect(brand.healthScore).toBe(63);
    expect(brand.dimensions.store).toBe(64);
    expect(brand.dimensionDetails?.supply?.assessment).toBe('Fulfillment gaps and stockout risk are limiting replenishment.');
    expect(brand.dimensionDetails?.margin?.weight).toBe(0.15);
  });

  it('opens the explanation panel with grounded content from the scorecard assessment', () => {
    renderWithProvider(<Harness />);

    fireEvent.click(screen.getByRole('button', { name: 'Explain Supply score' }));

    const panel = screen.getByRole('dialog');
    expect(within(panel).getByText("Why is Harvest Table's Supply score 35?")).toBeInTheDocument();
    expect(within(panel).getAllByText(/Fulfillment gaps and stockout risk are limiting replenishment/).length).toBeGreaterThan(0);
    expect(within(panel).getByText('supply-chain')).toBeInTheDocument();
  });

  it('builds different explanations for the brand and clicked dimension', () => {
    const brandExplanation = buildScorecardExplanation(harvestTable);
    const supplyExplanation = buildScorecardExplanation(harvestTable, 'supply');

    expect(brandExplanation.question).toBe("Why is Harvest Table's portfolio score 63?");
    expect(brandExplanation.answer).toContain('Demand 25%, Competitive 20%, Supply 20%, Store 20%, Margin 15%');
    expect(supplyExplanation.question).toBe("Why is Harvest Table's Supply score 35?");
    expect(supplyExplanation.answer).toContain('Fulfillment gaps and stockout risk are limiting replenishment');
    expect(supplyExplanation.answer).not.toContain('demand, margin, competitive, and supply metrics');
  });
});
