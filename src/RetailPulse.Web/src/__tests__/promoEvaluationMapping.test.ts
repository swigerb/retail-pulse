import { describe, it, expect } from 'vitest';
import { mapPromoEvaluation } from '../services/promoApi';

/**
 * `POST /api/taskmodule/promo` answers in flat snake_case; the planner UI models a
 * flattened camelCase `PromoEvaluation`. Nothing translated between them, so
 * `evaluation.risks` was `undefined` and `[...evaluation.risks]` in
 * PromoRecommendation threw "risks is not iterable", which killed the Campaign
 * Planner the moment an evaluation returned.
 *
 * These pin the mapping, and specifically that every array the component spreads or
 * maps over is always an array.
 */
describe('promo evaluation mapping', () => {
  const wire = {
    recommendation: 'recommended',
    risk_factors: [
      '2 overlapping campaign(s) detected',
      'Expected ROI below breakeven (1.0x)',
    ],
    roi_estimate: {
      roi: { expected: 2.4, lower_bound: 1.8, upper_bound: 3.1 },
      break_even_weeks: 3,
      inputs: { expected_lift_percent: 16.71 },
    },
    timing_assessment: {
      recommendation: 'Acceptable, review conflicts',
      timing_score: 0.6,
      seasonality_score: 0.8,
      conflicts: [{ campaign: 'Apex Grill BOGO Sep25' }],
      risks: [{ type: 'timing', detail: 'Overlaps a holiday freeze', severity: 'high' }],
    },
    lift_analysis: { reasoning: 'Lift is supported by prior comparable promos.' },
    historical_context: { total_campaigns: 7, campaigns: [{ roi: 2.0 }, { roi: 3.0 }] },
  };

  it('maps the flat wire shape onto the view model', () => {
    const e = mapPromoEvaluation(wire);

    expect(e.recommendation).toBe('recommended');
    expect(e.roi).toBe(2.4);
    expect(e.roiLower).toBe(1.8);
    expect(e.roiUpper).toBe(3.1);
    // break_even_weeks is reported in weeks; the panel labels days.
    expect(e.breakEvenDays).toBe(21);
    expect(e.similarCampaigns).toBe(7);
    expect(e.timingAssessment).toBe('Acceptable, review conflicts');
    expect(e.seasonalityFit).toContain('Strong seasonal fit');
    expect(e.reasoning).toContain('Lift is supported');
  });

  it('derives the historical average ROI from the returned campaigns', () => {
    expect(mapPromoEvaluation(wire).historicalAvgRoi).toBe(2.5);
  });

  it('merges structured timing risks with the flat risk_factors sentences', () => {
    const e = mapPromoEvaluation(wire);

    expect(Array.isArray(e.risks)).toBe(true);
    expect(e.risks).toHaveLength(3);
    expect(e.risks.map(r => r.detail)).toContain('Overlaps a holiday freeze');
    expect(e.risks.map(r => r.detail)).toContain('2 overlapping campaign(s) detected');

    // A sub-breakeven ROI is a hard problem, not an advisory note.
    const breakeven = e.risks.find(r => /below breakeven/i.test(r.detail));
    expect(breakeven?.severity).toBe('high');
  });

  it('flattens conflict objects into displayable strings', () => {
    const e = mapPromoEvaluation(wire);
    expect(e.conflicts).toEqual(['Apex Grill BOGO Sep25']);
  });

  // The regression that took the panel down: an empty or partial payload must still
  // produce iterable arrays, never undefined.
  it('never yields a non-iterable risks or conflicts array', () => {
    for (const payload of [{}, { recommendation: 'recommended' }, { timing_assessment: {} }]) {
      const e = mapPromoEvaluation(payload);
      expect(Array.isArray(e.risks)).toBe(true);
      expect(Array.isArray(e.conflicts)).toBe(true);
      expect(() => [...e.risks].sort()).not.toThrow();
    }
  });

  it('falls back to a safe recommendation level for an unknown value', () => {
    expect(mapPromoEvaluation({ recommendation: 'nonsense' }).recommendation)
      .toBe('insufficient_history');
  });

  it('marks missing ROI as insufficient history instead of rendering a confident zero', () => {
    const e = mapPromoEvaluation({
      recommendation: 'not_recommended',
      roi_estimate: {
        insufficient_history: true,
        message: 'Not enough comparable campaign history to model ROI for these inputs.',
      },
      historical_context: { total_campaigns: 0, campaigns: [] },
    });

    expect(e.recommendation).toBe('insufficient_history');
    expect(e.insufficientHistory).toBe(true);
    expect(e.roi).toBeNull();
    expect(e.roiLower).toBeNull();
    expect(e.roiUpper).toBeNull();
    expect(e.breakEvenDays).toBeNull();
    expect(e.historicalAvgRoi).toBeNull();
  });
});
