import { resolveApiUrl } from '../config/apiOrigin';
import type {
  CouncilAgentVote,
  CouncilConveneResponse,
  CouncilDisagreement,
  CouncilSession,
  CouncilVerdict,
  HealthRating,
} from '../types';

const HEALTH_RATINGS: readonly HealthRating[] = ['green', 'yellow', 'red'];

/**
 * Wire shapes returned by the council endpoints.
 *
 * The API answers in flat snake_case while the UI models a nested camelCase
 * `{ votes, verdict }`. Nothing translated the history response, so
 * `session.verdict` was undefined and the history panel threw as soon as it
 * rendered a real row.
 *
 * The mapping lives here, at the edge, so components keep consuming one stable
 * view model.
 */
interface WireVote {
  readonly agent_id?: unknown;
  readonly agent_name?: unknown;
  readonly rating?: unknown;
  readonly reasoning?: unknown;
  readonly confidence?: unknown;
  readonly key_metrics?: unknown;
  readonly response_time_ms?: unknown;
}

interface WireCouncilResponse {
  readonly id?: unknown;
  readonly brand?: unknown;
  readonly region?: unknown;
  readonly overall_rating?: unknown;
  readonly synthesis?: unknown;
  readonly is_unanimous?: unknown;
  readonly disagreements?: unknown;
  readonly action_items?: unknown;
  readonly convened_at?: unknown;
  readonly total_duration_ms?: unknown;
  readonly votes?: unknown;
}

function toStringValue(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function toOptionalString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function toNumberValue(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function toArray(value: unknown): readonly unknown[] {
  return Array.isArray(value) ? value : [];
}

function toWireVote(value: unknown): WireVote {
  return value && typeof value === 'object' ? value as WireVote : {};
}

function toWireCouncilResponse(value: unknown): WireCouncilResponse {
  return value && typeof value === 'object' ? value as WireCouncilResponse : {};
}

/**
 * Accept both the C# enum name and its numeric ordinal so a serializer change
 * cannot break style-map lookups again.
 */
function toRating(value: unknown): HealthRating {
  if (typeof value === 'number') return HEALTH_RATINGS[value] ?? 'yellow';
  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();
    const match = HEALTH_RATINGS.find((rating) => rating === normalized);
    if (match) return match;
  }
  return 'yellow';
}

/**
 * CouncilVoting keys its three cards on `domain`, which the wire format does not
 * carry. Derive it from the agent id (falling back to the display name) so a vote
 * binds to its card. Unknown agents fall back to 'demand' rather than being
 * dropped. A rendered card with a real rating beats one stuck deliberating.
 */
function toDomain(vote: WireVote): CouncilAgentVote['domain'] {
  const key = `${toStringValue(vote.agent_id)} ${toStringValue(vote.agent_name)}`.toLowerCase();
  if (key.includes('supply')) return 'supply';
  if (key.includes('competitive')) return 'competitive';
  return 'demand';
}

function toVote(value: unknown): CouncilAgentVote {
  const vote = toWireVote(value);
  return {
    agentId: toStringValue(vote.agent_id),
    agentName: toStringValue(vote.agent_name, 'Specialist'),
    domain: toDomain(vote),
    rating: toRating(vote.rating),
    confidence: toNumberValue(vote.confidence),
    reasoning: toStringValue(vote.reasoning),
    keyMetrics: toArray(vote.key_metrics).map((metric) => toStringValue(metric)).filter(Boolean),
    responseTimeMs: toNumberValue(vote.response_time_ms),
  };
}

/**
 * The API models a disagreement as a single sentence
 * ("Demand Forecasting rated Red, while Competitive Intelligence rated Yellow."),
 * not the structured topic/positions/resolution the UI type describes. Carry the
 * sentence through as the topic rather than regex-parsing it into a shape the
 * backend never promised; DisagreementHighlight renders the optional sections
 * only when they are populated.
 */
function toDisagreement(text: string): CouncilDisagreement {
  return {
    topic: text,
    agents: [],
    resolution: '',
    dominantAgent: '',
    dominantReason: '',
  };
}

/**
 * Action items arrive as pre-numbered sentences ("1. Immediately restore …").
 * Lift the ordinal into `priority` so the badge shows a number and the text is
 * not duplicated.
 */
function toDisagreementItem(value: unknown): CouncilDisagreement {
  return toDisagreement(toStringValue(value, 'Unspecified disagreement.'));
}

function toActionItem(value: unknown, index: number): { priority: number; text: string } {
  if (value && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    const text = toStringValue(record['text'], 'Review this council action.');
    return { priority: toNumberValue(record['priority'], index + 1), text };
  }

  const text = toStringValue(value, 'Review this council action.');
  const match = /^\s*(\d+)[.)]\s*([\s\S]*)$/.exec(text);
  if (match) {
    return { priority: Number(match[1]), text: match[2].trim() };
  }
  return { priority: index + 1, text: text.trim() };
}

function toVerdict(wire: WireCouncilResponse): CouncilVerdict {
  return {
    overallRating: toRating(wire.overall_rating),
    unanimous: wire.is_unanimous === true,
    synthesisText: toStringValue(wire.synthesis, 'No synthesis is available for this council session.'),
    disagreements: toArray(wire.disagreements).map(toDisagreementItem),
    actionItems: toArray(wire.action_items).map(toActionItem),
    totalConveneTimeMs: toNumberValue(wire.total_duration_ms),
  };
}

export function mapConveneResponse(value: unknown): CouncilConveneResponse {
  const wire = toWireCouncilResponse(value);
  return {
    sessionId: toStringValue(wire.convened_at),
    brand: toStringValue(wire.brand),
    region: toOptionalString(wire.region),
    votes: toArray(wire.votes).map(toVote),
    verdict: toVerdict(wire),
  };
}

export function toCouncilSession(value: unknown): CouncilSession {
  const wire = toWireCouncilResponse(value);
  const convenedAt = toStringValue(wire.convened_at);
  return {
    id: toStringValue(wire.id, convenedAt || 'unknown-council-session'),
    brand: toStringValue(wire.brand, 'Unknown brand'),
    region: toOptionalString(wire.region),
    convenedAt,
    votes: toArray(wire.votes).map(toVote),
    verdict: toVerdict(wire),
  };
}

export async function conveneCouncil(brand: string, region?: string): Promise<CouncilConveneResponse> {
  const res = await fetch(resolveApiUrl('/api/council/convene'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ brand, region }),
  });
  if (!res.ok) throw new Error(`Council convene failed: ${res.status}`);
  return mapConveneResponse(await res.json());
}

export async function fetchCouncilHistory(limit = 20): Promise<CouncilSession[]> {
  const res = await fetch(resolveApiUrl(`/api/council/history?limit=${limit}`));
  // The API does not map a council history route. Treat "no such surface" as an
  // empty history rather than surfacing a raw 404 inside the panel.
  if (res.status === 404) return [];
  if (!res.ok) throw new Error(`Failed to fetch council history: ${res.status}`);
  const wire = await res.json();
  return toArray(wire).map(toCouncilSession);
}
