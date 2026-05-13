import type { AdaptiveCard, CardComment, VoteChoice } from '../types';

const BASE = '/api/cards';

export async function fetchActiveCards(): Promise<AdaptiveCard[]> {
  const res = await fetch(BASE);
  if (!res.ok) throw new Error(`Failed to fetch cards: ${res.status}`);
  return res.json();
}

export async function submitVote(cardId: string, choice: VoteChoice): Promise<void> {
  const res = await fetch(`${BASE}/${cardId}/vote`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ choice }),
  });
  if (!res.ok) throw new Error(`Vote failed: ${res.status}`);
}

export async function addComment(cardId: string, text: string): Promise<CardComment> {
  const res = await fetch(`${BASE}/${cardId}/comments`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) throw new Error(`Comment failed: ${res.status}`);
  return res.json();
}
