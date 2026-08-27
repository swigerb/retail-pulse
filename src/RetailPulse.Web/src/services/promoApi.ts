import { resolveApiUrl } from '../config/apiOrigin';
import type { PromoFormData, PromoEvaluation, PromoCampaign, PromoRisk, PromoRecommendationLevel } from '../types';

/**
 * Wire shape returned by `POST /api/taskmodule/promo`.
 *
 * The endpoint orchestrates four MCP promo tools and returns their raw payloads
 * under flat snake_case keys. The planner UI models a flattened camelCase
 * `PromoEvaluation`, and nothing translated between them: `evaluation.risks` was
 * always `undefined`, so `[...evaluation.risks]` in PromoRecommendation threw
 * "risks is not iterable" and killed the panel the moment an evaluation returned.
 *
 * Mapping at the edge keeps the component consuming one stable view model.
 */
interface WirePromoEvaluation {
  readonly recommendation?: string;
  readonly risk_factors?: readonly string[];
  readonly roi_estimate?: {
    readonly expected_roi?: number;
    readonly roi_range?: { readonly low?: number; readonly high?: number };
    readonly break_even_days?: number;
    readonly historical_avg_roi?: number;
  };
  readonly timing_assessment?: {
    readonly assessment?: string;
    readonly conflicts?: readonly unknown[];
    readonly seasonality?: string;
    readonly risks?: readonly { readonly type?: string; readonly detail?: string; readonly severity?: string }[];
  };
  readonly lift_analysis?: { readonly reasoning?: string };
  readonly historical_context?: { readonly campaign_count?: number };
}

const RECOMMENDATION_LEVELS: ReadonlySet<string> = new Set([
  'strongly_recommended',
  'recommended',
  'proceed_with_caution',
  'not_recommended',
]);

function toRecommendation(value: string | undefined): PromoRecommendationLevel {
  const normalized = (value ?? '').trim().toLowerCase();
  return (RECOMMENDATION_LEVELS.has(normalized)
    ? normalized
    : 'proceed_with_caution') as PromoRecommendationLevel;
}

function toSeverity(value: string | undefined): PromoRisk['severity'] {
  const normalized = (value ?? '').trim().toLowerCase();
  return normalized === 'high' || normalized === 'low' ? normalized : 'medium';
}

/**
 * Risks arrive from two places: structured entries on the timing assessment, and
 * the endpoint's own flat `risk_factors` sentences (budget thresholds, sub-breakeven
 * ROI, campaign overlaps). Merge both so the panel shows the complete picture, and
 * give the flat sentences a severity rather than dropping them.
 */
function toRisks(wire: WirePromoEvaluation): PromoRisk[] {
  const structured: PromoRisk[] = (wire.timing_assessment?.risks ?? []).map(r => ({
    type: r.type ?? 'timing',
    detail: r.detail ?? '',
    severity: toSeverity(r.severity),
  }));

  const flat: PromoRisk[] = (wire.risk_factors ?? []).map(text => ({
    type: 'planning',
    detail: text,
    // The endpoint only emits a risk factor when a threshold was crossed, so
    // "medium" is the honest floor — these are not advisory notes.
    severity: /below breakeven|executive approval/i.test(text) ? 'high' : 'medium',
  }));

  // De-duplicate on detail so a risk surfaced by both paths is shown once.
  const seen = new Set<string>();
  return [...structured, ...flat].filter(r => {
    const key = r.detail.trim().toLowerCase();
    if (!key || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function toConflicts(wire: WirePromoEvaluation): string[] {
  return (wire.timing_assessment?.conflicts ?? []).map(c => {
    if (typeof c === 'string') return c;
    const obj = c as Record<string, unknown>;
    const name = obj.campaign ?? obj.name ?? obj.detail;
    return typeof name === 'string' ? name : JSON.stringify(c);
  });
}

export function mapPromoEvaluation(wire: WirePromoEvaluation): PromoEvaluation {
  const roi = wire.roi_estimate ?? {};
  return {
    recommendation: toRecommendation(wire.recommendation),
    roi: roi.expected_roi ?? 0,
    roiLower: roi.roi_range?.low ?? roi.expected_roi ?? 0,
    roiUpper: roi.roi_range?.high ?? roi.expected_roi ?? 0,
    reasoning: wire.lift_analysis?.reasoning ?? '',
    timingAssessment: wire.timing_assessment?.assessment ?? '',
    conflicts: toConflicts(wire),
    seasonalityFit: wire.timing_assessment?.seasonality ?? '',
    risks: toRisks(wire),
    similarCampaigns: wire.historical_context?.campaign_count ?? 0,
    breakEvenDays: roi.break_even_days ?? 0,
    historicalAvgRoi: roi.historical_avg_roi ?? 0,
  };
}

export async function evaluatePromo(data: PromoFormData): Promise<PromoEvaluation> {
  const res = await fetch(resolveApiUrl('/api/taskmodule/promo'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Failed to evaluate promo: ${res.status}`);
  return mapPromoEvaluation((await res.json()) as WirePromoEvaluation);
}

export async function fetchExistingCampaigns(): Promise<PromoCampaign[]> {
  const res = await fetch(resolveApiUrl('/api/campaigns'));
  // The campaign surface is optional. Treat "not mapped" as "no campaigns" rather
  // than throwing — an unhandled rejection here used to take the panel down.
  if (res.status === 404) return [];
  if (!res.ok) throw new Error(`Failed to fetch campaigns: ${res.status}`);
  return res.json();
}

export async function submitForApproval(
  formData: PromoFormData,
  evaluation: PromoEvaluation,
): Promise<void> {
  const res = await fetch(resolveApiUrl('/api/taskmodule/promo/submit'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ formData, evaluation }),
  });
  if (!res.ok) throw new Error(`Failed to submit for approval: ${res.status}`);
}
