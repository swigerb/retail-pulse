import { resolveApiUrl } from '../config/apiOrigin';
import type {
  BrandScore,
  BrandScoreDimensionDetail,
  MarginDriver,
  MarginWaterfallStep,
  ScorecardDimensionKey,
  StockoutRisk,
  StorePerformance,
} from '../types';
import { dimensionKeyFromAgent, SCORECARD_DIMENSION_CONFIG, SCORECARD_DIMENSION_ORDER } from '../scorecardModel';

/**
 * Financials and Store Operations reads.
 *
 * Both panels previously rendered hardcoded arrays declared inline in Dashboard.tsx
 * (`demoWaterfall`, `demoDrivers`, `demoStores`, `demoStockouts`) while the API had
 * been serving the real figures all along. Nothing was bound to them, so the demo
 * showed fabricated numbers that could not be reconciled with anything the system
 * actually knew.
 *
 * Every mapping here is written against a payload observed from the deployed API,
 * not inferred from surrounding code.
 */

async function getJson<T>(path: string): Promise<T | null> {
  const res = await fetch(resolveApiUrl(path));
  if (!res.ok) return null;
  return (await res.json()) as T;
}

// ── Financials ─────────────────────────────────────────────────────────────

interface WireFinancialPeriod {
  readonly period?: string;
  readonly revenue?: number;
  readonly cogs?: number;
  readonly marketing?: number;
  readonly distribution?: number;
  readonly grossMargin?: number;
  readonly netMargin?: number;
}

interface WireMarginBrand {
  readonly brand?: string;
  readonly financials?: readonly WireFinancialPeriod[];
}

interface WireDriver {
  readonly category?: string;
  readonly impact?: number;
  readonly trend?: string;
}

interface WireDrivers {
  readonly drivers?: readonly WireDriver[];
}

/**
 * Builds the P&L waterfall from the most recent reported period.
 *
 * The waterfall is a sequence of deltas, so costs are emitted as negatives and the
 * two subtotals (gross and net margin) are flagged — that is what makes the chart
 * read as a bridge rather than six unrelated bars.
 */
export function toWaterfall(wire: WireMarginBrand | null): MarginWaterfallStep[] {
  const periods = wire?.financials ?? [];
  if (periods.length === 0) return [];

  const latest = periods[periods.length - 1];
  return [
    { label: 'Revenue', value: latest.revenue ?? 0 },
    { label: 'COGS', value: -(latest.cogs ?? 0) },
    { label: 'Gross Margin', value: latest.grossMargin ?? 0, isSubtotal: true },
    { label: 'Marketing', value: -(latest.marketing ?? 0) },
    { label: 'Distribution', value: -(latest.distribution ?? 0) },
    { label: 'Net Margin', value: latest.netMargin ?? 0, isSubtotal: true },
  ];
}

/** The API reports increasing/decreasing/volatile/stable; the panel models improving/worsening/stable. */
function toTrend(trend: string | undefined, impact: number): MarginDriver['trend'] {
  const t = (trend ?? '').toLowerCase();
  if (t === 'stable') return 'stable';
  // "increasing"/"decreasing" describe the cost, not the margin, so the sign of the
  // margin impact is the honest signal for whether this driver is helping or hurting.
  if (impact > 0) return 'improving';
  if (impact < 0) return 'worsening';
  return 'stable';
}

export function toDrivers(wire: WireDrivers | null): MarginDriver[] {
  return (wire?.drivers ?? []).map(d => {
    const impact = d.impact ?? 0;
    return {
      name: d.category ?? 'Unknown',
      impact,
      trend: toTrend(d.trend, impact),
      // A driver dragging margin down by more than a point is worth flagging.
      isRisk: impact <= -1,
    };
  });
}

export async function fetchFinancials(brand: string): Promise<{
  waterfall: MarginWaterfallStep[];
  drivers: MarginDriver[];
  period: string;
}> {
  const [marginRaw, driversRaw] = await Promise.all([
    getJson<WireMarginBrand>(`/api/margin/${encodeURIComponent(brand)}`),
    getJson<WireDrivers>(`/api/margin/drivers/${encodeURIComponent(brand)}`),
  ]);

  const periods = marginRaw?.financials ?? [];
  return {
    waterfall: toWaterfall(marginRaw),
    drivers: toDrivers(driversRaw),
    period: periods.length > 0 ? (periods[periods.length - 1].period ?? '') : '',
  };
}

// ── Store operations ───────────────────────────────────────────────────────

interface WireStore {
  readonly storeId?: string;
  readonly storeName?: string;
  readonly region?: string;
  readonly revenue?: number;
  readonly target?: number;
  readonly performanceIndex?: number;
  readonly issues?: readonly string[];
}

interface WireStores {
  readonly stores?: readonly WireStore[];
}

/**
 * MCP reports issues but not remedies. Derive an actionable next step from the
 * issue set so the Recommendations column carries system-derived advice instead of
 * the fixed strings the panel used to hardcode.
 */
function toRecommendations(issues: readonly string[], index: number): string[] {
  const out: string[] = [];
  const has = (needle: string) => issues.some(i => i.toLowerCase().includes(needle));

  if (has('significantly below')) out.push('Urgent restock and pricing review');
  else if (has('below target')) out.push('Increase weekday promotions');
  if (has('conversion')) out.push('Audit display compliance');
  if (has('foot traffic')) out.push('Local marketing push');

  if (out.length === 0) out.push(index >= 1.05 ? 'Expand premium shelf space' : 'Maintain current plan');
  return out;
}

export function toStores(wire: WireStores | null): StorePerformance[] {
  return (wire?.stores ?? []).map(s => {
    const issues = [...(s.issues ?? [])];
    const index = s.performanceIndex ?? 0;
    return {
      storeId: s.storeId ?? '',
      storeName: s.storeName ?? '',
      region: s.region ?? '',
      revenue: s.revenue ?? 0,
      target: s.target ?? 0,
      // MCP reports a ratio (0.838); the panel renders a percentage.
      performanceIndex: Math.round(index * 100),
      issues,
      recommendations: toRecommendations(issues, index),
    };
  });
}

export async function fetchStores(): Promise<StorePerformance[]> {
  return toStores(await getJson<WireStores>('/api/stores/performance'));
}

export async function fetchStockoutRisks(): Promise<StockoutRisk[]> {
  return (await getJson<StockoutRisk[]>('/api/stores/stockout-risks')) ?? [];
}

// ── Portfolio scorecard ────────────────────────────────────────────────────

interface WireDimension {
  readonly dimension?: string;
  readonly score?: number;
  readonly weight?: number;
  readonly weightedScore?: number;
  readonly assessment?: string;
  readonly agentKey?: string;
}

interface WireBrandScore {
  readonly brand?: string;
  readonly overallScore?: number;
  readonly dimensions?: Record<string, WireDimension>;
  readonly summary?: string;
  readonly actionItems?: readonly string[];
  readonly durationMs?: number;
}

interface WireScorecard {
  readonly brands?: readonly WireBrandScore[];
  readonly totalDurationMs?: number;
}

function toDimensionDetails(wire: Record<string, WireDimension> | undefined): Partial<Record<ScorecardDimensionKey, BrandScoreDimensionDetail>> {
  const byKey: Partial<Record<ScorecardDimensionKey, BrandScoreDimensionDetail>> = {};
  for (const entry of Object.values(wire ?? {})) {
    const key = dimensionKeyFromAgent(entry?.agentKey);
    if (!key) continue;
    const config = SCORECARD_DIMENSION_CONFIG[key];
    const score = Math.round((entry.score ?? 0) * 10);
    const weight = entry.weight ?? config.weight;
    byKey[key] = {
      key,
      label: entry.dimension ?? config.label,
      shortLabel: config.shortLabel,
      score,
      weight,
      weightedScore: (entry.weightedScore ?? ((entry.score ?? 0) * weight)) * 10,
      assessment: entry.assessment,
      agentKey: entry.agentKey ?? config.agentKey,
    };
  }
  return byKey;
}

function toDimensions(details: Partial<Record<ScorecardDimensionKey, BrandScoreDimensionDetail>>): BrandScore['dimensions'] {
  return SCORECARD_DIMENSION_ORDER.reduce((acc, key) => {
    acc[key] = Math.round(details[key]?.score ?? 0);
    return acc;
  }, {} as BrandScore['dimensions']);
}

export function toBrandScores(wire: WireScorecard | null): BrandScore[] {
  return (wire?.brands ?? []).map(b => {
    const dimensionDetails = toDimensionDetails(b.dimensions);
    const dims = toDimensions(dimensionDetails);
    const entries = Object.values(dimensionDetails).map(d => [d.key, d.score] as const);
    // The orchestrator returns a summary and actions, not an explicit risk/opportunity
    // pair. Derive them from the weakest and strongest dimensions so the card reports
    // something traceable to the scores it is showing.
    const weakest = entries.reduce<typeof entries[number] | undefined>(
      (a, b2) => (!a || b2[1] < a[1] ? b2 : a),
      undefined,
    );
    const strongest = entries.reduce<typeof entries[number] | undefined>(
      (a, b2) => (!a || b2[1] > a[1] ? b2 : a),
      undefined,
    );
    const actions = b.actionItems ?? [];
    // overallScore is reported 0-10; the panel's health score is 0-100.
    const score = (b.overallScore ?? 0) * 10;

    return {
      brandName: b.brand ?? '',
      healthScore: Math.round(score),
      trend: score >= 70 ? 'up' : score >= 50 ? 'stable' : 'down',
      dimensions: dims,
      dimensionDetails,
      summary: b.summary,
      actionItems: [...actions],
      durationMs: b.durationMs,
      topRisk: actions[0] ?? `${weakest ? SCORECARD_DIMENSION_CONFIG[weakest[0]].shortLabel : 'Scorecard'} is the weakest dimension (${weakest?.[1] ?? 0})`,
      topOpportunity: actions[1] ?? `Build on ${strongest ? SCORECARD_DIMENSION_CONFIG[strongest[0]].shortLabel : 'scorecard'} strength (${strongest?.[1] ?? 0})`,
    } satisfies BrandScore;
  });
}

export async function fetchScorecard(brands: readonly string[]): Promise<{
  brands: BrandScore[];
  durationMs: number;
}> {
  const res = await fetch(resolveApiUrl('/api/scorecard'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ brands, includeSummary: false }),
  });
  // Surface the failure. Returning an empty batch silently left the panel spinning
  // with no indication that anything had gone wrong.
  if (!res.ok) throw new Error(`Scorecard request failed: ${res.status}`);
  const wire = (await res.json()) as WireScorecard;
  return { brands: toBrandScores(wire), durationMs: wire.totalDurationMs ?? 0 };
}

/**
 * Number of brands scored per request.
 *
 * There is a hard 45s ceiling on the request path and per-brand latency is highly
 * variable (measured 4s-33s for a single brand against the deployed backend). Two
 * brands in one request has been observed to breach the ceiling outright, so the
 * portfolio is sent one brand at a time. The server caches each brand's score, so
 * the cost is paid once and revisits are near-instant.
 */
const SCORECARD_BATCH_SIZE = 1;

/**
 * Scores a portfolio in ceiling-safe batches, reporting each batch as it lands so
 * the panel fills in progressively instead of showing nothing until the last
 * brand is done.
 */
export async function fetchScorecardBatched(
  brands: readonly string[],
  onBatch: (scored: BrandScore[], elapsedMs: number) => void,
): Promise<void> {
  const started = Date.now();
  const accumulated: BrandScore[] = [];

  for (let i = 0; i < brands.length; i += SCORECARD_BATCH_SIZE) {
    const batch = brands.slice(i, i + SCORECARD_BATCH_SIZE);
    const { brands: scored } = await fetchScorecard(batch);
    accumulated.push(...scored);
    // Highest health first, so the ranking stays meaningful as batches arrive.
    onBatch(
      [...accumulated].sort((a, b) => b.healthScore - a.healthScore),
      Date.now() - started,
    );
  }
}
