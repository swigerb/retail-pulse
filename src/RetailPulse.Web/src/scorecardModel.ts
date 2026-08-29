import type { BrandScore, BrandScoreDimensionDetail, ExplanationData, ScorecardDimensionKey } from './types';

export const SCORECARD_DIMENSION_ORDER: ScorecardDimensionKey[] = [
  'demand',
  'competitive',
  'supply',
  'store',
  'margin',
];

export const SCORECARD_DIMENSION_CONFIG: Record<ScorecardDimensionKey, {
  label: string;
  shortLabel: string;
  agentKey: string;
  weight: number;
  measures: string;
}> = {
  demand: {
    label: 'Demand Momentum',
    shortLabel: 'Demand',
    agentKey: 'demand-forecasting',
    weight: 0.25,
    measures: 'customer pull, sell-through, and near-term forecast strength',
  },
  competitive: {
    label: 'Competitive Position',
    shortLabel: 'Competitive',
    agentKey: 'competitive-intel',
    weight: 0.20,
    measures: 'market share, pricing pressure, and competitor activity',
  },
  supply: {
    label: 'Supply Reliability',
    shortLabel: 'Supply',
    agentKey: 'supply-chain',
    weight: 0.20,
    measures: 'inventory, shipments, fulfillment, and stockout risk',
  },
  store: {
    label: 'Store Execution',
    shortLabel: 'Store',
    agentKey: 'store-ops',
    weight: 0.20,
    measures: 'store performance, availability, placement, and operational follow-through',
  },
  margin: {
    label: 'Margin Health',
    shortLabel: 'Margin',
    agentKey: 'margin-analysis',
    weight: 0.15,
    measures: 'gross margin, cost pressure, and profit resilience',
  },
};

export function dimensionKeyFromAgent(agentKey: string | undefined): ScorecardDimensionKey | undefined {
  return SCORECARD_DIMENSION_ORDER.find(key => SCORECARD_DIMENSION_CONFIG[key].agentKey === agentKey);
}

export function normalizeDimensionKey(value: string | undefined): ScorecardDimensionKey | undefined {
  if (!value) return undefined;
  const lower = value.toLowerCase();
  return SCORECARD_DIMENSION_ORDER.find(key =>
    key === lower
    || SCORECARD_DIMENSION_CONFIG[key].label.toLowerCase() === lower
    || SCORECARD_DIMENSION_CONFIG[key].shortLabel.toLowerCase() === lower
    || SCORECARD_DIMENSION_CONFIG[key].agentKey.toLowerCase() === lower);
}

export function getCompositeBand(score: number): { label: string; description: string } {
  if (score > 75) return { label: 'strong', description: 'the brand is performing well overall' };
  if (score >= 50) return { label: 'mixed performance', description: 'the brand has real strengths but needs follow-up' };
  return { label: 'needs attention', description: 'the brand is underperforming across enough signals to require action' };
}

export function getDimensionBand(score: number): string {
  if (score >= 80) return 'excellent';
  if (score >= 60) return 'good';
  if (score >= 40) return 'mixed';
  if (score >= 20) return 'poor';
  return 'critical';
}

function isGroundedAssessment(assessment: string | undefined): assessment is string {
  if (!assessment?.trim()) return false;
  return !/^(agent unavailable|assessment timed out|assessment failed|no assessment provided)/i.test(assessment.trim());
}

function cleanAssessment(assessment: string): string {
  return assessment.trim().replace(/[.!?]+$/u, '');
}

export function getBrandDimensionDetails(brand: BrandScore): BrandScoreDimensionDetail[] {
  const keys = brand.dimensionDetails && Object.keys(brand.dimensionDetails).length > 0
    ? SCORECARD_DIMENSION_ORDER.filter(key => brand.dimensionDetails?.[key])
    : SCORECARD_DIMENSION_ORDER;
  return keys.map(key => {
    const config = SCORECARD_DIMENSION_CONFIG[key];
    const fromPayload = brand.dimensionDetails?.[key];
    const score = fromPayload?.score ?? brand.dimensions[key] ?? 0;
    return {
      key,
      label: fromPayload?.label ?? config.label,
      shortLabel: fromPayload?.shortLabel ?? config.shortLabel,
      agentKey: fromPayload?.agentKey ?? config.agentKey,
      score,
      weight: fromPayload?.weight ?? config.weight,
      weightedScore: fromPayload?.weightedScore ?? score * config.weight,
      assessment: fromPayload?.assessment,
    };
  });
}

export function formatWeightPercent(weight: number): string {
  return `${Math.round(weight * 100)}%`;
}

export function formatCompositeCalculation(details: BrandScoreDimensionDetail[], roundedScore: number): string {
  const weights = details
    .map(d => `${d.shortLabel} ${formatWeightPercent(d.weight)}`)
    .join(', ');
  const contributions = details
    .map(d => `${d.shortLabel} ${d.weightedScore.toFixed(1)}`)
    .join(' + ');
  return `The composite multiplies each 0 to 100 dimension score by its weight and adds the weighted points. Weights are ${weights}; for this brand that is ${contributions}, rounded to ${roundedScore}.`;
}

export function describeDimension(detail: BrandScoreDimensionDetail, brandName: string): string {
  const band = getDimensionBand(detail.score);
  const base = `${detail.shortLabel} measures ${SCORECARD_DIMENSION_CONFIG[detail.key].measures}; at ${Math.round(detail.score)}/100, ${brandName} is in the ${band} band for this dimension`;
  const assessment = detail.assessment;
  if (isGroundedAssessment(assessment)) {
    return `${base}, and the specialist assessment says "${cleanAssessment(assessment)}".`;
  }
  return `${base}, but the scorecard payload did not include a grounded specialist assessment explaining the number.`;
}

export function getTrendDisclosure(brand: BrandScore): string {
  const label = brand.trend === 'up' ? 'up' : brand.trend === 'down' ? 'down' : 'steady';
  return `Arrow: ${label}. The scorecard payload does not include a measured baseline, magnitude, or time period, so this arrow should be read as a current score-band indicator rather than a period-over-period trend.`;
}

export function buildScorecardExplanation(
  brand: BrandScore,
  requestedDimension?: string,
): ExplanationData {
  const dimensionKey = normalizeDimensionKey(requestedDimension);
  const details = getBrandDimensionDetails(brand);
  const selected = dimensionKey ? details.find(d => d.key === dimensionKey) : undefined;
  const grounded = details.filter(d => isGroundedAssessment(d.assessment));
  const coverage = details.length > 0 ? Math.round((grounded.length / details.length) * 100) : 0;

  if (selected) {
    const band = getDimensionBand(selected.score);
    const selectedAssessment = selected.assessment;
    const groundedSelected = isGroundedAssessment(selectedAssessment);
    return {
      question: `Why is ${brand.brandName}'s ${selected.shortLabel} score ${Math.round(selected.score)}?`,
      answer: groundedSelected
        ? `${selected.shortLabel} is ${Math.round(selected.score)}/100, which is ${band}. ${describeDimension(selected, brand.brandName)} It carries ${formatWeightPercent(selected.weight)} of the composite score.`
        : `${selected.shortLabel} is ${Math.round(selected.score)}/100, which is ${band}. The scorecard did not return a grounded specialist explanation for this dimension, so the panel is showing the source and weighting only. It carries ${formatWeightPercent(selected.weight)} of the composite score.`,
      steps: [
        {
          toolName: selected.agentKey,
          inputSummary: `${brand.brandName} scored for ${selected.label}`,
          outputSummary: `${Math.round(selected.score)}/100 at ${formatWeightPercent(selected.weight)} weight`,
          reasoning: groundedSelected ? cleanAssessment(selectedAssessment) : 'No grounded specialist assessment was returned for this dimension.',
        },
      ],
      confidence: groundedSelected ? 100 : 0,
      dataSources: [{ name: `${selected.label} specialist output (${selected.agentKey})` }],
      generatedAt: new Date().toISOString(),
    };
  }

  return {
    question: `Why is ${brand.brandName}'s portfolio score ${brand.healthScore}?`,
    answer: `${brand.brandName} scores ${brand.healthScore}/100, which is ${getCompositeBand(brand.healthScore).label}. ${formatCompositeCalculation(details, brand.healthScore)} ${getTrendDisclosure(brand)}`,
    steps: details.map(d => ({
      toolName: d.agentKey,
      inputSummary: `${brand.brandName} scored for ${d.label}`,
      outputSummary: `${Math.round(d.score)}/100 contributes ${d.weightedScore.toFixed(1)} points`,
      reasoning: (() => {
        const assessment = d.assessment;
        return isGroundedAssessment(assessment)
          ? cleanAssessment(assessment)
          : 'No grounded specialist assessment was returned for this dimension.';
      })(),
    })),
    confidence: coverage,
    dataSources: details.map(d => ({ name: `${d.label} specialist output (${d.agentKey})` })),
    generatedAt: new Date().toISOString(),
  };
}
