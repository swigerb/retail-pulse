import { resolveApiUrl } from '../config/apiOrigin';
import type {
  CouncilAgentVote,
  CouncilConveneResponse,
  CouncilDisagreement,
  CouncilSession,
  CouncilVerdict,
  HealthRating,
} from '../types';

/**
 * Wire shapes returned by `POST /api/council/convene`.
 *
 * The API answers in flat snake_case (see ChatEndpoints) while the UI models a
 * nested camelCase `{ votes, verdict }`. Nothing translated between them, so
 * `response.verdict` was always `undefined` — the executive verdict never
 * rendered — and every vote arrived without a `domain`, which is the key
 * CouncilVoting matches its three cards on, so all three sat on
 * "DELIBERATING…" forever even though the council had already reported.
 *
 * The mapping lives here, at the edge, so components keep consuming one stable
 * view model.
 */
interface WireVote {
  readonly agent_id?: string;
  readonly agent_name?: string;
  readonly rating?: string;
  readonly reasoning?: string;
  readonly confidence?: number;
  readonly key_metrics?: readonly string[];
  readonly response_time_ms?: number;
}

interface WireConveneResponse {
  readonly brand?: string;
  readonly region?: string;
  readonly overall_rating?: string;
  readonly synthesis?: string;
  readonly is_unanimous?: boolean;
  readonly disagreements?: readonly string[];
  readonly action_items?: readonly string[];
  readonly convened_at?: string;
  readonly total_duration_ms?: number;
  readonly votes?: readonly WireVote[];
}

/** `HealthRating` is lowercase in the UI; the API sends the C# enum name ("Yellow"). */
function toRating(value: string | undefined): HealthRating {
  switch ((value ?? '').trim().toLowerCase()) {
    case 'green':
      return 'green';
    case 'red':
      return 'red';
    default:
      return 'yellow';
  }
}

/**
 * CouncilVoting keys its three cards on `domain`, which the wire format does not
 * carry. Derive it from the agent id (falling back to the display name) so a vote
 * binds to its card. Unknown agents fall back to 'demand' rather than being
 * dropped — a rendered card with a real rating beats one stuck deliberating.
 */
function toDomain(vote: WireVote): CouncilAgentVote['domain'] {
  const key = `${vote.agent_id ?? ''} ${vote.agent_name ?? ''}`.toLowerCase();
  if (key.includes('supply')) return 'supply';
  if (key.includes('competitive')) return 'competitive';
  return 'demand';
}

function toVote(vote: WireVote): CouncilAgentVote {
  return {
    agentId: vote.agent_id ?? '',
    agentName: vote.agent_name ?? 'Specialist',
    domain: toDomain(vote),
    rating: toRating(vote.rating),
    confidence: vote.confidence ?? 0,
    reasoning: vote.reasoning ?? '',
    keyMetrics: [...(vote.key_metrics ?? [])],
    responseTimeMs: vote.response_time_ms ?? 0,
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
function toActionItem(text: string, index: number): { priority: number; text: string } {
  const match = /^\s*(\d+)[.)]\s*([\s\S]*)$/.exec(text);
  if (match) {
    return { priority: Number(match[1]), text: match[2].trim() };
  }
  return { priority: index + 1, text: text.trim() };
}

function toVerdict(wire: WireConveneResponse): CouncilVerdict {
  return {
    overallRating: toRating(wire.overall_rating),
    unanimous: wire.is_unanimous ?? false,
    synthesisText: wire.synthesis ?? '',
    disagreements: (wire.disagreements ?? []).map(toDisagreement),
    actionItems: (wire.action_items ?? []).map(toActionItem),
    totalConveneTimeMs: wire.total_duration_ms ?? 0,
  };
}

export function mapConveneResponse(wire: WireConveneResponse): CouncilConveneResponse {
  return {
    sessionId: wire.convened_at ?? '',
    brand: wire.brand ?? '',
    region: wire.region,
    votes: (wire.votes ?? []).map(toVote),
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
  return mapConveneResponse((await res.json()) as WireConveneResponse);
}

export async function fetchCouncilHistory(limit = 20): Promise<CouncilSession[]> {
  const res = await fetch(resolveApiUrl(`/api/council/history?limit=${limit}`));
  // The API does not map a council history route. Treat "no such surface" as an
  // empty history rather than surfacing a raw 404 inside the panel.
  if (res.status === 404) return [];
  if (!res.ok) throw new Error(`Failed to fetch council history: ${res.status}`);
  return res.json();
}
