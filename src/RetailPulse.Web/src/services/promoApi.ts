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
    readonly lower_bound?: number;
    readonly upper_bound?: number;
    readonly roi?: {
      readonly expected?: number;
      readonly lower_bound?: number;
      readonly upper_bound?: number;
    };
    readonly break_even_weeks?: number;
    readonly break_even_days?: number;
    readonly insufficient_history?: boolean;
    readonly message?: string;
    readonly similar_campaigns?: number;
    readonly historical_avg_roi?: number;
    readonly basis?: string;
    readonly inputs?: { readonly expected_lift_percent?: number };
  };
  readonly timing_assessment?: {
    readonly recommendation?: string;
    readonly timing_score?: number;
    readonly seasonality_score?: number;
    readonly conflicts?: readonly unknown[];
    readonly risks?: readonly { readonly type?: string; readonly detail?: string; readonly severity?: string }[];
  };
  readonly lift_analysis?: {
    readonly reasoning?: string;
    readonly summary?: string;
    readonly expected_lift_percent?: number;
  };
  readonly historical_context?: {
    readonly total_campaigns?: number;
    readonly avg_roi?: number;
    readonly basis?: string;
    readonly message?: string;
    readonly campaigns?: readonly { readonly roi?: number }[];
  };
}

const RECOMMENDATION_LEVELS: ReadonlySet<string> = new Set([
  'recommended',
  'cautious',
  'not_recommended',
  'insufficient_history',
]);

function toRecommendation(value: string | undefined): PromoRecommendationLevel {
  const normalized = (value ?? '').trim().toLowerCase();
  if (normalized === 'strongly_recommended') return 'recommended';
  if (normalized === 'proceed_with_caution') return 'cautious';
  return (RECOMMENDATION_LEVELS.has(normalized) ? normalized : 'cautious') as PromoRecommendationLevel;
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

  // RiskCard shows `type` as the headline and reveals `detail` on expand, so the
  // flat sentences must carry their text in `type`, otherwise every planning risk
  // renders as the literal word "planning" with the real reason hidden behind a click.
  const flat: PromoRisk[] = (wire.risk_factors ?? []).map(text => ({
    type: text,
    detail: text,
    // The endpoint only emits a risk factor when a threshold was crossed, so
    // "medium" is the honest floor because these are not advisory notes.
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

/** Renders the 0..1 seasonality score as a phrase the panel can display. */
function toSeasonalityFit(score: number | undefined): string {
  if (score === undefined) return '';
  if (score >= 0.75) return `Strong seasonal fit (${Math.round(score * 100)}%)`;
  if (score >= 0.5) return `Acceptable seasonal fit (${Math.round(score * 100)}%)`;
  return `Weak seasonal fit (${Math.round(score * 100)}%)`;
}

function numberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

export function mapPromoEvaluation(wire: WirePromoEvaluation): PromoEvaluation {
  const est = wire.roi_estimate ?? {};
  const nestedRoi = est.roi ?? {};
  const hist = wire.historical_context ?? {};
  const roi = numberOrNull(nestedRoi.expected) ?? numberOrNull(est.expected_roi);
  const roiLower = numberOrNull(nestedRoi.lower_bound) ?? numberOrNull(est.lower_bound);
  const roiUpper = numberOrNull(nestedRoi.upper_bound) ?? numberOrNull(est.upper_bound);
  const historyCount = hist.total_campaigns ?? 0;
  const modelCount = est.similar_campaigns ?? 0;
  const similarCampaigns = modelCount > 0 ? modelCount : historyCount;
  const hasModeledRoi = roi !== null && roiLower !== null && roiUpper !== null;
  const insufficientHistory = est.insufficient_history === true || !hasModeledRoi;

  // The MCP payload reports break-even in weeks; the panel labels it in days.
  const breakEvenDays = insufficientHistory
    ? null
    : est.break_even_days
      ?? (est.break_even_weeks !== undefined ? Math.max(1, Math.round(est.break_even_weeks * 7)) : null);

  // No aggregate average is published, so derive it from the returned campaigns
  // rather than showing a hardcoded zero.
  const campaignRois = (hist.campaigns ?? [])
    .map(c => c.roi)
    .filter((r): r is number => typeof r === 'number');
  const historicalAvgRoi = insufficientHistory
    ? null
    : est.historical_avg_roi
      ?? hist.avg_roi
      ?? (campaignRois.length > 0
      ? campaignRois.reduce((a, b) => a + b, 0) / campaignRois.length
      : null);

  const historyMessage = est.message
    ?? hist.message
    ?? 'Not enough comparable campaign history to model ROI for these inputs.';

  return {
    recommendation: insufficientHistory ? 'insufficient_history' : toRecommendation(wire.recommendation),
    roi,
    roiLower,
    roiUpper,
    reasoning: insufficientHistory
      ? historyMessage
      : wire.lift_analysis?.reasoning
        ?? wire.lift_analysis?.summary
        ?? (wire.lift_analysis?.expected_lift_percent !== undefined
          ? `Modelled lift of ${wire.lift_analysis.expected_lift_percent}% against the regional baseline.`
          : ''),
    timingAssessment: wire.timing_assessment?.recommendation ?? '',
    conflicts: toConflicts(wire),
    seasonalityFit: toSeasonalityFit(wire.timing_assessment?.seasonality_score),
    risks: toRisks(wire),
    similarCampaigns,
    breakEvenDays,
    historicalAvgRoi,
    insufficientHistory,
    historyMessage,
    dataBasis: est.basis ?? hist.basis,
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
  // than throwing because an unhandled rejection here used to take the panel down.
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
