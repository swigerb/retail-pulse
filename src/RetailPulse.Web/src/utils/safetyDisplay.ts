import type {
  BlockedRequest,
  GuardrailDetectionType,
  SafetyBlockDisplayModel,
  SafetyBlockFamily,
  SafetyBlockStage,
  SafetyCategoryName,
  SafetyDecisionKind,
} from '../types';

/**
 * Safe display helpers for issue #101 — every function here is deliberately
 * pure and only accepts / returns whitelisted fields. Raw regex patterns,
 * threshold values, analyzer names, rule IDs, or any bypass-useful strings
 * must never be constructed or propagated by this module.
 */

const PATTERN_DETECTION_TYPES: ReadonlySet<GuardrailDetectionType> = new Set([
  'jailbreak',
  'injection',
  'pii',
  'access',
  'agent-definition-jailbreak',
]);

const MODEL_DETECTION_TYPES: ReadonlySet<GuardrailDetectionType> = new Set([
  'content-safety-hate',
  'content-safety-sexual',
  'content-safety-violence',
  'content-safety-selfharm',
  'content-safety-prompt-shield',
  'content-safety-indirect-injection',
  'content-safety-unavailable',
  'content-safety-block',
  'agent-definition-content-safety',
  'agent-definition-content-safety-unavailable',
]);

/** Detection types the frontend recognises. Unknown types render as a
 *  generic safety block via the `unknown` family. */
export function classifyBlockFamily(detectionType: string | undefined | null): SafetyBlockFamily {
  if (!detectionType) return 'unknown';
  const typed = detectionType as GuardrailDetectionType;
  if (PATTERN_DETECTION_TYPES.has(typed)) return 'pattern';
  if (MODEL_DETECTION_TYPES.has(typed)) return 'model';
  // Legacy prefix fallback so a newly-added `content-safety-*` type still
  // classifies as model-based without a code change on the frontend.
  if (detectionType.startsWith('content-safety-')) return 'model';
  return 'unknown';
}

/**
 * Content Safety severity axis is 0/2/4/6. We deliberately map these to
 * user-facing words instead of publishing the numeric axis — the numeric
 * value is what an attacker would need to iterate around the filter.
 */
export function describeSeverity(severity: number | undefined | null): SafetyBlockDisplayModel['severityLabel'] | undefined {
  if (severity === undefined || severity === null) return undefined;
  if (severity <= 1) return 'low';
  if (severity <= 3) return 'medium';
  if (severity <= 5) return 'high';
  return 'severe';
}

const CATEGORY_LABELS: Record<SafetyCategoryName, string> = {
  Hate: 'Hateful content',
  Sexual: 'Sexual content',
  Violence: 'Violent content',
  SelfHarm: 'Self-harm content',
};

const DETECTION_LABELS: Partial<Record<GuardrailDetectionType, string>> = {
  'content-safety-hate': 'Hateful content',
  'content-safety-sexual': 'Sexual content',
  'content-safety-violence': 'Violent content',
  'content-safety-selfharm': 'Self-harm content',
  'content-safety-prompt-shield': 'Prompt-shield safety',
  'content-safety-indirect-injection': 'Indirect injection',
  'content-safety-unavailable': 'Safety service unavailable',
  'content-safety-block': 'Content Safety',
  'agent-definition-content-safety': 'Agent definition safety',
  'agent-definition-content-safety-unavailable': 'Safety service unavailable',
  'agent-definition-jailbreak': 'Prompt jailbreak',
  'agent-definition-structural': 'Agent definition structure',
  'agent-definition-policy': 'Agent definition policy',
  'agent-definition-privileged-grant': 'Privileged tool grant',
  jailbreak: 'Prompt jailbreak',
  injection: 'SQL or script injection',
  pii: 'Personal information',
  access: 'Restricted data',
};

/** Normalises a backend `Category` string to the known category name union. */
export function normaliseCategoryName(category: string | undefined | null): SafetyCategoryName | undefined {
  if (!category) return undefined;
  const lowered = category.trim().toLowerCase();
  if (lowered === 'hate') return 'Hate';
  if (lowered === 'sexual') return 'Sexual';
  if (lowered === 'violence') return 'Violence';
  if (lowered === 'selfharm' || lowered === 'self-harm' || lowered === 'self harm') return 'SelfHarm';
  return undefined;
}

/** Plain-language category label — never returns raw category codes. */
export function describeCategory(
  category: SafetyCategoryName | string | undefined | null,
  detectionType?: GuardrailDetectionType | string | null,
): string | undefined {
  const normalised = normaliseCategoryName(category ?? undefined);
  if (normalised) return CATEGORY_LABELS[normalised];
  if (detectionType) {
    const label = DETECTION_LABELS[detectionType as GuardrailDetectionType];
    if (label) return label;
  }
  return undefined;
}

const STAGE_REASONS: Record<SafetyBlockStage, string> = {
  input: 'Your request was blocked by our content safety layer.',
  output: 'The response was withheld by our content safety layer before it could be shown.',
  'plan-step': 'This plan step was blocked by our content safety layer.',
  ingestion: 'This document was quarantined by our content safety layer during ingestion.',
  'retrieved-knowledge': 'A knowledge passage was withheld by our content safety layer.',
  'tool-result': 'A tool result was withheld by our content safety layer.',
};

function normaliseDecision(decision: string | undefined | null): SafetyDecisionKind | undefined {
  if (!decision) return undefined;
  const lowered = decision.trim().toLowerCase();
  if (lowered === 'blocked') return 'Blocked';
  if (lowered === 'flagged') return 'Flagged';
  if (lowered === 'serviceunavailable' || lowered === 'service_unavailable' || lowered === 'unavailable') return 'ServiceUnavailable';
  if (lowered === 'passed') return 'Passed';
  return undefined;
}

/**
 * Builds a display model from an already-whitelisted set of safe fields.
 * The caller is responsible for never passing raw pattern / threshold text
 * into `reason` or `suggestion` — the type system cannot enforce that.
 */
export function buildSafetyBlockDisplay(input: {
  stage: SafetyBlockStage;
  detectionType?: GuardrailDetectionType | string | null;
  category?: SafetyCategoryName | string | null;
  severity?: number | null;
  decision?: string | null;
  reason?: string | null;
  suggestion?: string | null;
  failClosed?: boolean | null;
}): SafetyBlockDisplayModel {
  const family = classifyBlockFamily(input.detectionType);
  const categoryName = normaliseCategoryName(input.category ?? undefined);
  const categoryLabel = describeCategory(input.category ?? undefined, input.detectionType ?? undefined);
  const severityLabel = describeSeverity(input.severity ?? undefined);
  const decision = normaliseDecision(input.decision);

  const isUnavailable =
    decision === 'ServiceUnavailable' || input.detectionType === 'content-safety-unavailable';

  let reason = input.reason?.trim();
  if (!reason || reason.length === 0) {
    if (isUnavailable) {
      reason = input.failClosed
        ? 'The content safety layer is temporarily unavailable, and this deployment is configured to fail closed. Please retry shortly.'
        : 'The content safety layer was temporarily unavailable when this request ran.';
    } else {
      reason = STAGE_REASONS[input.stage];
    }
  }

  return {
    stage: input.stage,
    family,
    reason,
    suggestion: input.suggestion?.trim() || undefined,
    categoryLabel,
    categoryName,
    severityLabel,
    decision,
    modelBased: family === 'model',
    failClosed: input.failClosed ?? undefined,
  };
}

/**
 * Builds a display model from a `BlockedRequest` audit-log row. Only the
 * whitelisted fields (detectionType, category, severity, decision) are
 * consulted — the raw `requestPreview` and `reason` free-text fields are
 * treated as user-supplied strings and are NOT forwarded into the display
 * model's rendered `reason`.
 */
export function buildSafetyDisplayFromBlockedRequest(
  entry: BlockedRequest,
  stage: SafetyBlockStage = 'input',
): SafetyBlockDisplayModel {
  return buildSafetyBlockDisplay({
    stage,
    detectionType: entry.detectionType,
    category: entry.category,
    severity: entry.severity,
    decision: entry.decision,
  });
}

// ── Refusal-text detection ────────────────────────────────────────────────
// The backend refusal templates (`GuardrailsMiddleware._defaultRefusal` and
// `_unavailableRefusal` in the API project) are the only text the chat
// endpoint sends the frontend on a blocked path. We recognise them here so
// the chat panel can promote them to a rich safety block instead of showing
// them as raw markdown.

const DEFAULT_REFUSAL_PREFIX =
  "I can't help with that request. My guardrails detected potentially harmful content";
const UNAVAILABLE_REFUSAL_PREFIX =
  "I can't process that request right now. The content safety layer is temporarily unavailable";

/** Backend refusal-type labels the frontend maps to safe display models. */
const REFUSAL_TYPE_TO_DETECTION: Record<string, GuardrailDetectionType> = {
  'jailbreak attempt': 'jailbreak',
  'potential injection': 'jailbreak',
  'content safety': 'content-safety-prompt-shield',
};

/**
 * Returns a safety display model when `reply` matches one of the backend
 * refusal templates, otherwise `null`. The reply's raw text is NEVER
 * forwarded into `reason` — the frontend produces its own plain-language
 * copy so the chat surface stays consistent.
 */
export function detectSafetyRefusal(reply: string): SafetyBlockDisplayModel | null {
  if (!reply) return null;
  const trimmed = reply.trim();

  if (trimmed.startsWith(UNAVAILABLE_REFUSAL_PREFIX)) {
    return buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-unavailable',
      decision: 'ServiceUnavailable',
      failClosed: true,
    });
  }

  if (trimmed.startsWith(DEFAULT_REFUSAL_PREFIX)) {
    // The template embeds the refusal type in parentheses. We match against a
    // small whitelist rather than echoing the substring, so an accidentally
    // over-broad backend edit cannot leak new detail through the display.
    const match = trimmed.match(/detected potentially harmful content \(([^)]+)\)\./i);
    const rawType = match ? match[1].trim().toLowerCase() : undefined;
    const detectionType = rawType ? REFUSAL_TYPE_TO_DETECTION[rawType] : undefined;
    return buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: detectionType ?? 'content-safety-prompt-shield',
      decision: 'Blocked',
    });
  }

  return null;
}

// ── Family aggregation for the dashboard ──────────────────────────────────

export interface SafetyFamilyAggregate {
  pattern: number;
  model: number;
}

/**
 * Splits a `BlockedRequest` list into pattern-based vs model-based counts.
 * Deliberately re-classifies from `detectionType` rather than trusting the
 * caller to have pre-tagged the family, so a rogue future entry cannot show
 * up in the wrong bucket.
 */
export function aggregateByFamily(entries: readonly BlockedRequest[]): SafetyFamilyAggregate {
  let pattern = 0;
  let model = 0;
  for (const entry of entries) {
    const family = classifyBlockFamily(entry.detectionType);
    if (family === 'model') model += 1;
    else if (family === 'pattern') pattern += 1;
  }
  return { pattern, model };
}

export interface CategoryAggregate {
  category: SafetyCategoryName;
  label: string;
  count: number;
}

/** Counts model-family entries by well-known safety category. */
export function aggregateByCategory(entries: readonly BlockedRequest[]): CategoryAggregate[] {
  const counts: Record<SafetyCategoryName, number> = { Hate: 0, Sexual: 0, Violence: 0, SelfHarm: 0 };
  for (const entry of entries) {
    if (classifyBlockFamily(entry.detectionType) !== 'model') continue;
    const name = normaliseCategoryName(entry.category)
      ?? deriveCategoryFromDetectionType(entry.detectionType);
    if (name) counts[name] += 1;
  }
  return (Object.keys(counts) as SafetyCategoryName[]).map(category => ({
    category,
    label: CATEGORY_LABELS[category],
    count: counts[category],
  }));
}

function deriveCategoryFromDetectionType(detectionType: string): SafetyCategoryName | undefined {
  switch (detectionType) {
    case 'content-safety-hate':
      return 'Hate';
    case 'content-safety-sexual':
      return 'Sexual';
    case 'content-safety-violence':
      return 'Violence';
    case 'content-safety-selfharm':
      return 'SelfHarm';
    default:
      return undefined;
  }
}

export type SeverityBucket = 'low' | 'medium' | 'high' | 'severe';

export interface SeverityAggregate {
  bucket: SeverityBucket;
  label: string;
  count: number;
}

const SEVERITY_LABELS: Record<SeverityBucket, string> = {
  low: 'Low',
  medium: 'Medium',
  high: 'High',
  severe: 'Severe',
};

/**
 * Buckets model-family entries by severity descriptor. Entries with no
 * severity data (pattern-family, or Content Safety entries without a
 * per-category hit) are counted as `low` so the chart still sums to the
 * total model-family count.
 */
export function aggregateBySeverity(entries: readonly BlockedRequest[]): SeverityAggregate[] {
  const counts: Record<SeverityBucket, number> = { low: 0, medium: 0, high: 0, severe: 0 };
  for (const entry of entries) {
    if (classifyBlockFamily(entry.detectionType) !== 'model') continue;
    const bucket = describeSeverity(entry.severity ?? undefined) ?? 'low';
    counts[bucket] += 1;
  }
  return (Object.keys(counts) as SeverityBucket[]).map(bucket => ({
    bucket,
    label: SEVERITY_LABELS[bucket],
    count: counts[bucket],
  }));
}
