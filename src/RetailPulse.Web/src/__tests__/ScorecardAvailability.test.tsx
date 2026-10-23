import { describe, expect, it, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { toBrandScores, fetchScorecard, fetchScorecardBatched } from '../services/operationsApi';
import { buildScorecardExplanation, SCORECARD_DIMENSION_CONFIG, SCORECARD_DIMENSION_ORDER } from '../scorecardModel';
import { PortfolioScorecard } from '../components/scorecard/PortfolioScorecard';
import { BrandScoreCard } from '../components/scorecard/BrandScoreCard';

function wireBrand(available: number, status?: string) {
  return {
    brand: 'FreshMart',
    // Deliberately retain the old fallback number to prove invalid evidence cannot
    // become a measured composite even during a rolling deployment.
    overallScore: 5,
    status,
    dimensions: Object.fromEntries(SCORECARD_DIMENSION_ORDER.map((key, i) => {
      const config = SCORECARD_DIMENSION_CONFIG[key];
      return [config.label, {
        dimension: config.label,
        agentKey: config.agentKey,
        score: i < available ? 8 : 5,
        weight: config.weight,
        weightedScore: (i < available ? 8 : 5) * config.weight,
        assessment: i < available ? 'Reported sell-through increased.' : 'Assessment timed out — using neutral score.',
      }];
    })),
    actionItems: ['Invented fallback risk', 'Invented fallback opportunity'],
    summary: 'FreshMart shows mixed performance.',
  };
}

afterEach(() => vi.unstubAllGlobals());

describe('scorecard availability contract', () => {
  it.each([0, 2, 5])('maps %i available dimensions without inventing missing values', available => {
    const [brand] = toBrandScores({ brands: [wireBrand(available)] });
    expect(brand.status).toBe(available === 5 ? 'complete' : available > 0 ? 'partial' : 'unavailable');
    expect(brand.healthScore).toBe(available === 5 ? 50 : null);
    expect(brand.dimensions.demand).toBe(available > 0 ? 80 : null);
    expect(brand.dimensions.margin).toBe(available === 5 ? 80 : null);
    if (available < 5) {
      expect(brand.topRisk).toBe('');
      expect(brand.topOpportunity).toBe('');
      expect(brand.actionItems).toEqual([]);
      expect(brand.summary).not.toContain('mixed performance');
      expect(brand.dimensionDetails?.margin?.weightedScore).toBeNull();
      const explanation = buildScorecardExplanation(brand);
      expect(explanation.answer).toContain('Overall score unavailable');
      expect(explanation.answer).not.toContain('mixed performance');
      expect(explanation.confidence).toBe(available * 20);
      expect(explanation.dataSources).toHaveLength(available);
      expect(explanation.steps?.filter(s => s.outputSummary.startsWith('Unavailable'))).toHaveLength(5 - available);
    }
  });

  it('renders all-unavailable cards and explanations without numbers, arrows, business pills or radar', () => {
    const [brand] = toBrandScores({ brands: [wireBrand(0, 'unavailable')] });
    const { container } = render(
      <FluentProvider theme={webDarkTheme}>
        <PortfolioScorecard brands={[brand]} />
        <BrandScoreCard brand={brand} />
      </FluentProvider>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Assessment unavailable');
    expect(screen.getAllByText('N/A')).toHaveLength(5);
    expect(screen.queryByText('50')).not.toBeInTheDocument();
    expect(screen.queryByText('→')).not.toBeInTheDocument();
    expect(screen.queryByText(/Invented fallback/)).not.toBeInTheDocument();
    expect(container.querySelector('svg')).toBeNull();
    const explanation = buildScorecardExplanation(brand, 'supply');
    expect(explanation.question).toContain('assessment unavailable');
    expect(explanation.answer).toContain('timed out');
    expect(explanation.answer).not.toContain('/100');
    expect(explanation.confidence).toBe(0);
    expect(explanation.dataSources).toEqual([]);
  });

  it('renders partial assessment with preserved dimensions and no composite comparison', () => {
    const [brand] = toBrandScores({ brands: [wireBrand(2, 'partial')] });
    render(<FluentProvider theme={webDarkTheme}>
      <PortfolioScorecard brands={[brand]} />
      <BrandScoreCard brand={brand} />
    </FluentProvider>);
    expect(screen.getByRole('status')).toHaveTextContent('Partial assessment');
    expect(screen.getAllByText('80')).toHaveLength(2);
    expect(screen.getAllByText('N/A')).toHaveLength(3);
    expect(screen.queryByText(/weakest dimension/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Build on/)).not.toBeInTheDocument();
    expect(buildScorecardExplanation(brand, 'demand').answer).toContain('80/100');
  });

  it.each([
    { score: null, assessment: 'Reported increase.' },
    { score: 11, assessment: 'Reported increase.' },
    { score: 8, assessment: '' },
    { score: 8, assessment: 7 },
    { score: 8, assessment: null },
    { score: 5, assessment: 'No grounded specialist assessment was returned for this dimension.' },
    { score: 8, assessment: 'Reported increase.', status: 'unavailable' },
  ])('rejects invalid dimension evidence %j', dimension => {
    const wire = wireBrand(5, 'complete');
    Object.assign(wire.dimensions['Demand Momentum'], dimension);
    const [brand] = toBrandScores({ brands: [wire] });
    expect(brand.healthScore).toBeNull();
    expect(brand.dimensions.demand).toBeNull();
    expect(brand.status).toBe('partial');
  });

  it('does not coerce absent dimensions or an absent composite into zero', () => {
    const [brand] = toBrandScores({ brands: [{ brand: 'FreshMart' }] });
    expect(brand.healthScore).toBeNull();
    expect(Object.values(brand.dimensions)).toEqual([null, null, null, null, null]);
    expect(brand.topRisk).toBe('');
    expect(brand.topOpportunity).toBe('');
  });

  it('consumes explicit API nulls and preserves request contract', async () => {
    const wire = wireBrand(0, 'unavailable');
    const payload = { brands: [{ ...wire, overallScore: null, dimensions: {
      'Demand Momentum': { ...wire.dimensions['Demand Momentum'], score: null, weightedScore: null, status: 'unavailable' },
    } }], totalDurationMs: 30 };
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(payload)));
    vi.stubGlobal('fetch', fetchMock);
    const result = await fetchScorecard(['FreshMart']);
    expect(result.brands[0].healthScore).toBeNull();
    expect(result.durationMs).toBe(30);
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ brands: ['FreshMart'], includeSummary: false });
  });

  it('ranks complete scores ahead of unavailable scores as batches land', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ brands: [wireBrand(0, 'unavailable')] })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ brands: [{ ...wireBrand(5, 'complete'), brand: 'HealthyBrand' }] }))));
    const onBatch = vi.fn();
    await fetchScorecardBatched(['FreshMart', 'HealthyBrand'], onBatch);
    expect(onBatch.mock.calls[1][0].map((b: { brandName: string }) => b.brandName)).toEqual(['HealthyBrand', 'FreshMart']);
  });
});
