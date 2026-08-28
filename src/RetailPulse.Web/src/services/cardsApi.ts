import { resolveApiUrl } from '../config/apiOrigin';
import type {
  AdaptiveCard,
  CardComment,
  CardLifecycleState,
  CardType,
  UserVote,
  VoteChoice,
} from '../types';

const BASE = '/api/cards';

/**
 * Collaborative card reads and writes.
 *
 * The server and the SPA disagreed about this contract in three ways at once, and
 * because the card list was always empty, none of it ever surfaced:
 *
 *  - Enums cross the wire as INTEGERS (`type: 0`, `lifecycle: 1`), while the panel
 *    keys its style maps by lowercase names. Looking up `CARD_LIFECYCLE_CONFIG[1]`
 *    returned undefined and the panel crashed reading `.bg`.
 *  - The server sends `lifecycle`; the panel reads `state`.
 *  - The server sends votes as `{ vote, timestamp }`; the panel expects
 *    `{ choice, votedAt }`. Comments have no `id` server-side at all.
 *
 * Everything is normalised here, at the edge, so components consume one stable view
 * model. The orderings below mirror the C# enums exactly — see
 * RetailPulse.Contracts/Cards/IAdaptiveCardState.cs.
 */
const CARD_TYPES: readonly CardType[] = ['voting', 'drilldown', 'dashboard', 'briefing'];
const CARD_LIFECYCLES: readonly CardLifecycleState[] = ['active', 'voting', 'decided', 'archived'];

interface WireVote {
  readonly userId?: string;
  readonly userName?: string;
  readonly vote?: string;
  readonly timestamp?: string;
}

interface WireComment {
  readonly userId?: string;
  readonly userName?: string;
  readonly text?: string;
  readonly timestamp?: string;
}

interface WireCard {
  readonly id?: string;
  readonly title?: string;
  readonly type?: number | string;
  readonly lifecycle?: number | string;
  readonly createdBy?: string;
  readonly createdAt?: string;
  readonly votes?: readonly WireVote[];
  readonly comments?: readonly WireComment[];
  readonly data?: Record<string, unknown>;
  readonly escalationReason?: string | null;
}

/**
 * Accepts either the integer the server sends today or a name, so adding a
 * JsonStringEnumConverter later cannot silently break the panel again.
 */
function toEnum<T extends string>(
  raw: number | string | undefined,
  order: readonly T[],
  fallback: T,
): T {
  if (typeof raw === 'number') return order[raw] ?? fallback;
  if (typeof raw === 'string') {
    const match = order.find((v) => v === raw.toLowerCase());
    if (match) return match;
  }
  return fallback;
}

/** Council agent votes carry a health rating ("Red"), not an approve/reject choice. */
function toChoice(vote: string | undefined): VoteChoice {
  const v = (vote ?? '').toLowerCase();
  if (v === 'approve' || v === 'green') return 'approve';
  if (v === 'reject' || v === 'red') return 'reject';
  return 'abstain';
}

function toVote(w: WireVote): UserVote {
  return {
    userId: w.userId ?? '',
    userName: w.userName ?? 'Unknown',
    choice: toChoice(w.vote),
    votedAt: w.timestamp ?? '',
  };
}

function toComment(w: WireComment, index: number): CardComment {
  return {
    // The server does not assign comment ids, so derive a stable one for React keys.
    id: `${w.userId ?? 'anon'}-${w.timestamp ?? index}`,
    userId: w.userId ?? '',
    userName: w.userName ?? 'Unknown',
    text: w.text ?? '',
    timestamp: w.timestamp ?? '',
  };
}

/** Cards carry their detail in a free-form data bag; surface a readable summary. */
function toSummary(data: Record<string, unknown> | undefined): string {
  const synthesis = data?.['synthesis'];
  if (typeof synthesis === 'string' && synthesis.length > 0) return synthesis;

  const brand = data?.['brand'];
  const rating = data?.['overall_rating'];
  if (typeof brand === 'string' && typeof rating === 'string') {
    return `${brand} assessed as ${rating}.`;
  }
  return '';
}

export function toCard(w: WireCard): AdaptiveCard {
  return {
    id: w.id ?? '',
    title: w.title ?? 'Untitled card',
    type: toEnum(w.type, CARD_TYPES, 'voting'),
    state: toEnum(w.lifecycle, CARD_LIFECYCLES, 'active'),
    summary: toSummary(w.data),
    // The server does not track a separate lifecycle-change timestamp.
    stateChangedAt: w.createdAt ?? '',
    createdAt: w.createdAt ?? '',
    createdBy: w.createdBy ?? '',
    votes: (w.votes ?? []).map(toVote),
    comments: (w.comments ?? []).map(toComment),
    data: w.data ?? {},
    escalated: Boolean(w.escalationReason),
    escalationReason: w.escalationReason ?? undefined,
  };
}

export async function fetchActiveCards(): Promise<AdaptiveCard[]> {
  const res = await fetch(resolveApiUrl(BASE));
  if (!res.ok) throw new Error(`Failed to fetch cards: ${res.status}`);
  const wire = (await res.json()) as WireCard[];
  return wire.map(toCard);
}

export async function submitVote(cardId: string, choice: VoteChoice): Promise<AdaptiveCard> {
  const res = await fetch(resolveApiUrl(`${BASE}/${cardId}/vote`), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ choice }),
  });
  if (!res.ok) throw new Error(`Vote failed: ${res.status}`);
  return toCard((await res.json()) as WireCard);
}

export async function addComment(cardId: string, text: string): Promise<CardComment> {
  const res = await fetch(resolveApiUrl(`${BASE}/${cardId}/comments`), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) throw new Error(`Comment failed: ${res.status}`);
  return toComment((await res.json()) as WireComment, 0);
}
