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
