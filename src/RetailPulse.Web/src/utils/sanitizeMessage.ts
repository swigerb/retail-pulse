/**
 * Defense-in-depth sanitization for chat messages.
 * Strips internal tool-call artifacts that may leak from the backend
 * before content is rendered to the user.
 */

// Matches `to=functions.FunctionName` patterns (with optional surrounding whitespace)
const TOOL_CALL_PREFIX_RE = /to=functions\.\w+\s*/g;

// Matches raw JSON-like function call blocks: `{"key":"value"...}` preceded by tool markers
// Only strip when preceded by a tool call pattern on the same logical block
const TOOL_CALL_BLOCK_RE = /^to=functions\.\w+[^\n]*?\{[\s\S]*?\}\s*/gm;

// Matches garbled Unicode text commonly seen in tool-call leakage (CJK characters mixed with latin in tool context)
// Captures the entire line including any trailing JSON block
const GARBLED_TOOL_CONTEXT_RE = /[\u4e00-\u9fff\u3400-\u4dbf]{2,}[^\n]*?(json|functions)[^\n]*\n*/gi;

// Chart types the frontend can render. Mirrors the backend ChartSpecNormalizer so
// that a leaked chart-spec payload is recognized (and stripped) the same way.
const CHART_TYPES = new Set([
  'line',
  'bar',
  'groupedbar',
  'stackedbar',
  'horizontalbar',
  'pie',
  'donut',
  'gauge',
  'table',
]);

/**
 * Yields the [start, end) spans of every top-level balanced JSON object in the
 * text, honoring string literals and escapes. Truncated objects are skipped.
 */
function findJsonObjectSpans(text: string): Array<[number, number]> {
  const spans: Array<[number, number]> = [];
  let i = 0;
  const n = text.length;
  while (i < n) {
    if (text[i] !== '{') {
      i++;
      continue;
    }
    let depth = 0;
    let inString = false;
    let escaped = false;
    const start = i;
    let j = i;
    for (; j < n; j++) {
      const c = text[j];
      if (inString) {
        if (escaped) escaped = false;
        else if (c === '\\') escaped = true;
        else if (c === '"') inString = false;
        continue;
      }
      if (c === '"') inString = true;
      else if (c === '{') depth++;
      else if (c === '}') {
        depth--;
        if (depth === 0) {
          j++;
          break;
        }
      }
    }
    if (depth === 0 && j > start) {
      spans.push([start, j]);
      i = j;
    } else {
      i = start + 1;
    }
  }
  return spans;
}

// Property names the backend ChartSpecNormalizer accepts for a datapoint's x/y.
// Mirrored here so frontend bindability matches backend strictness exactly.
const POINT_Y_KEYS = ['y', 'value', 'count'];

/**
 * Parses a value to a finite number the same way the backend does: numeric
 * JSON values and numeric strings bind; everything else (null, bool, empty
 * string, non-numeric text) does not.
 */
function toBindableNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (trimmed.length === 0) return null;
    const parsed = Number(trimmed);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

// A datapoint object binds only when it carries a numeric y/value/count, matching
// ChartSpecNormalizer.TryGetPointY (x is optional and falls back to an index).
function pointObjectHasValue(point: Record<string, unknown>): boolean {
  return POINT_Y_KEYS.some((key) => key in point && toBindableNumber(point[key]) !== null);
}

// A "values"/"data" array yields a bindable point when any element is a bindable
// number or an object with a numeric y/value/count — mirrors BuildSeriesFromValues.
function valuesArrayHasPoint(values: unknown): boolean {
  if (!Array.isArray(values)) return false;
  return values.some((v) =>
    v !== null && typeof v === 'object' && !Array.isArray(v)
      ? pointObjectHasValue(v as Record<string, unknown>)
      : toBindableNumber(v) !== null,
  );
}

// A single series object binds when its "values" (preferred) or "data" array
// yields at least one point — mirrors ChartSpecNormalizer.BuildSeries.
function seriesObjectHasPoint(series: Record<string, unknown>): boolean {
  const values = 'values' in series ? series.values : 'data' in series ? series.data : undefined;
  return valuesArrayHasPoint(values);
}

// A top-level/inner "series" array binds when any entry (a bare number array or a
// { name, values|data } object) yields a point — mirrors SeriesFromSeriesArray.
function seriesArrayHasPoint(seriesArray: unknown[]): boolean {
  return seriesArray.some((entry) => {
    if (Array.isArray(entry)) return valuesArrayHasPoint(entry);
    if (entry !== null && typeof entry === 'object') {
      return seriesObjectHasPoint(entry as Record<string, unknown>);
    }
    return false;
  });
}

/**
 * Returns true when the chart object has at least one actually bindable
 * datapoint across the schemas the backend ChartSpecNormalizer supports:
 *  - canonical: `data: [{ legend, values: [{ x, y } | number] }]`
 *  - labels/series object: `data: { labels, series: [{ name, values }] }`
 *    (or a single-series `data: { labels, values }`)
 *  - full-config: top-level `series: [{ name, data | values }]`
 *
 * Empty/null/non-renderable payloads (e.g. `data: []`, `data: null`,
 * `data: { id: 1 }`) yield false so the prose is left visible, exactly as the
 * backend rejects them.
 */
function chartHasBindableData(obj: Record<string, unknown>): boolean {
  if ('data' in obj) {
    const data = obj.data;
    if (Array.isArray(data)) {
      return data.some(
        (el) =>
          el !== null &&
          typeof el === 'object' &&
          !Array.isArray(el) &&
          seriesObjectHasPoint(el as Record<string, unknown>),
      );
    }
    if (data !== null && typeof data === 'object') {
      const dataObj = data as Record<string, unknown>;
      if (Array.isArray(dataObj.series)) return seriesArrayHasPoint(dataObj.series);
      if ('values' in dataObj) return valuesArrayHasPoint(dataObj.values);
      return false;
    }
    // data is null or a primitive — nothing bindable.
    return false;
  }

  return Array.isArray(obj.series) ? seriesArrayHasPoint(obj.series) : false;
}

/**
 * Returns true when a parsed value looks like a chart specification the backend
 * should have promoted to structured `charts` — a recognized chart `type`, a
 * non-empty `title`, and at least one bindable datapoint. Deliberately mirrors
 * the backend ChartSpecNormalizer so arbitrary, empty, or non-renderable JSON
 * (which the backend rejects and leaves visible) is never silently deleted.
 */
function looksLikeChartSpec(value: unknown): boolean {
  if (typeof value !== 'object' || value === null) return false;
  const obj = value as Record<string, unknown>;
  const type = obj.type;
  const title = obj.title;
  if (
    typeof type !== 'string' ||
    !CHART_TYPES.has(type.trim().toLowerCase()) ||
    typeof title !== 'string' ||
    title.trim().length === 0
  ) {
    return false;
  }
  return chartHasBindableData(obj);
}

/**
 * Removes any chart-spec JSON block the model echoed into its prose. This is a
 * last-line guard: the backend already extracts inline chart JSON and renders it
 * via ChartRenderer, so a well-behaved response never reaches here with chart
 * JSON. When it does (e.g. a stale backend or a streamed token race), we strip
 * only recognizable chart payloads and leave all other JSON intact.
 */
function stripChartJson(content: string): string {
  const spans = findJsonObjectSpans(content);
  if (spans.length === 0) return content;

  const removals: Array<[number, number]> = [];
  for (const [start, end] of spans) {
    try {
      if (looksLikeChartSpec(JSON.parse(content.slice(start, end)))) {
        removals.push([start, end]);
      }
    } catch {
      // Not valid JSON — leave it untouched.
    }
  }
  if (removals.length === 0) return content;

  let result = '';
  let cursor = 0;
  for (const [start, end] of removals) {
    if (start > cursor) result += content.slice(cursor, start);
    cursor = end;
  }
  if (cursor < content.length) result += content.slice(cursor);

  // Collapse code fences left empty after their JSON body was removed.
  return result.replace(/```(?:json)?\s*```/gi, '');
}

/**
 * Sanitizes an assistant message by removing tool-call artifacts.
 * Returns the cleaned string with leading/trailing whitespace trimmed.
 */
export function sanitizeMessage(content: string): string {
  if (!content) return content;

  let cleaned = content;

  // Remove full tool-call blocks (to=functions.X ... {json})
  cleaned = cleaned.replace(TOOL_CALL_BLOCK_RE, '');

  // Remove any remaining `to=functions.*` fragments
  cleaned = cleaned.replace(TOOL_CALL_PREFIX_RE, '');

  // Remove garbled Unicode + tool context lines
  cleaned = cleaned.replace(GARBLED_TOOL_CONTEXT_RE, '');

  // Strip any chart-spec JSON the model narrated instead of emitting as a chart.
  cleaned = stripChartJson(cleaned);

  // Collapse multiple blank lines into one
  cleaned = cleaned.replace(/\n{3,}/g, '\n\n');

  return cleaned.trim();
}
