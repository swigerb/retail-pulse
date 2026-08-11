import type { ChartSpec, ChartSeries } from '../types';

function seriesHasFinitePoint(series: ChartSeries): boolean {
  return (
    !!series &&
    typeof series.legend === 'string' &&
    series.legend.trim().length > 0 &&
    Array.isArray(series.values) &&
    series.values.some((v) => v != null && typeof v.y === 'number' && Number.isFinite(v.y))
  );
}

/**
 * True when a ChartSpec can actually be drawn: a typed spec with a non-empty
 * title and at least one legend-bearing series carrying a finite datapoint.
 *
 * This is the frontend half of the shared renderable-chart invariant. It guards
 * the actual P0 defect (issue #32): a spec with no bindable datapoints must
 * surface a diagnostic, never an empty chart card with bare axes and no marks.
 *
 * Recognized-type enforcement is owned authoritatively by the backend
 * `ChartSpecValidator`, which drops unknown types before a chart is ever
 * emitted. The frontend intentionally stays permissive on the type string so
 * the renderer's table fallback can still surface a data-bearing spec rather
 * than hide real data; we only require the type to be a non-empty string.
 */
export function chartIsRenderable(spec: ChartSpec | null | undefined): boolean {
  if (!spec || typeof spec.type !== 'string' || spec.type.trim().length === 0) {
    return false;
  }
  if (typeof spec.title !== 'string' || spec.title.trim().length === 0) {
    return false;
  }
  return Array.isArray(spec.data) && spec.data.some(seriesHasFinitePoint);
}

// Ranking-style chart types must clear a minimum mark count to be worth
// painting — a "top brands" chart with only 1–2 marks is a broken aggregate,
// not a ranking. Kept tenant-generic; no prompt-specific special casing.
const RANKING_CHART_TYPES = new Set(['horizontalbar']);
const RANKING_MIN_MARKS = 6;

// Chart types where a zero / non-finite payload renders as an empty shell in
// Recharts (bars vanish, line has no visible curve, pie/donut collapses). For
// these we require at least one non-zero magnitude to paint. Gauge and table
// are intentionally excluded — a zero-value gauge or an empty-cell table row
// is still a meaningful, non-broken visualization.
const MAGNITUDE_SENSITIVE_CHART_TYPES = new Set([
  'bar',
  'groupedbar',
  'stackedbar',
  'horizontalbar',
  'line',
  'pie',
  'donut',
]);

function countFiniteNonZeroPoints(series: ChartSeries): number {
  if (!series || !Array.isArray(series.values)) return 0;
  let n = 0;
  for (const v of series.values) {
    if (v != null && typeof v.y === 'number' && Number.isFinite(v.y) && v.y !== 0) {
      n += 1;
    }
  }
  return n;
}

/**
 * True when a ChartSpec carries enough non-zero, finite magnitude to actually
 * paint visible marks in Recharts.
 *
 * `chartIsRenderable` intentionally treats `0` as a legal finite datapoint
 * (gauges, KPI-style single-value charts). But a bar/line chart whose entire
 * payload is zero (or non-finite) will render an empty shell — axes, no
 * `.recharts-bar-rectangle` marks — with no diagnostic. That is the P0 defect
 * behind issue #74.
 *
 * This helper is the stricter sibling used at the render boundary to decide
 * whether a chart shell should paint or fall back to the shared
 * `chart-unavailable` note:
 *
 *   • Every series must contribute at least one finite, non-zero point;
 *     otherwise there is no visible magnitude to draw.
 *   • Ranking-style chart types (horizontalBar) additionally require at least
 *     `RANKING_MIN_MARKS` such points across all series — a "ranking" of 1–2
 *     brands is a broken aggregate, not a chart worth surfacing.
 *
 * Tenant-generic: no prompt strings, no metric names, no tenant-specific
 * thresholds. Backend contract tests own the "was the right data collected"
 * question; this guards the render boundary only.
 */
export function chartHasVisibleMagnitude(spec: ChartSpec | null | undefined): boolean {
  if (!chartIsRenderable(spec)) return false;
  const type = spec!.type.trim().toLowerCase();
  // For types that don't depend on non-zero magnitude to paint (gauge, table,
  // and any unrecognized type that falls back to the table renderer), defer to
  // chartIsRenderable — zero is a legal datapoint there.
  if (!MAGNITUDE_SENSITIVE_CHART_TYPES.has(type)) return true;

  const series = spec!.data;
  if (!series.length) return false;

  let totalNonZero = 0;
  for (const s of series) {
    const nonZero = countFiniteNonZeroPoints(s);
    if (nonZero === 0) return false;
    totalNonZero += nonZero;
  }

  if (RANKING_CHART_TYPES.has(type) && totalNonZero < RANKING_MIN_MARKS) {
    return false;
  }
  return true;
}
