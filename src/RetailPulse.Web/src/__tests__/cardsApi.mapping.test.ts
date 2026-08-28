import { describe, it, expect } from 'vitest';
import { toCard } from '../services/cardsApi';

/**
 * The Cards panel crashed the moment it was given a real card, because the list had
 * always been empty and the render path had never executed against live data.
 *
 * Three contract mismatches were stacked on top of each other. These tests pin each
 * one against the payload the API actually returns.
 */
describe('cardsApi wire mapping', () => {
  // Captured verbatim from the deployed API after convening a council.
  const wire = {
    id: 'card-abc123',
    title: 'Council Verdict: Summit Vodka — Red',
    type: 0,
    lifecycle: 1,
    createdBy: 'council-orchestrator',
    createdAt: '2026-08-28T02:40:50.2235061Z',
    votes: [
      { userId: 'supply-chain', userName: 'Supply Chain', vote: 'Red', timestamp: '2026-08-28T02:40:50Z' },
      { userId: 'demand-forecasting', userName: 'Demand Forecasting', vote: 'Green', timestamp: '2026-08-28T02:40:50Z' },
    ],
    comments: [
      { userId: 'u1', userName: 'Brian Swiger', text: 'Escalating.', timestamp: '2026-08-28T02:40:51Z' },
    ],
    data: { brand: 'Summit Vodka', overall_rating: 'Red', synthesis: 'Supply reliability is the binding constraint.' },
    escalationReason: null,
  };

  it('decodes integer enums into the names the panel styles by', () => {
    const card = toCard(wire);
    // CardType 0 = Voting, CardLifecycle 1 = Voting. Passing the raw integers through
    // made CARD_LIFECYCLE_CONFIG[1] undefined and the panel threw on `.bg`.
    expect(card.type).toBe('voting');
    expect(card.state).toBe('voting');
  });

  it('reads the server field named lifecycle into the panel field named state', () => {
    expect(toCard({ ...wire, lifecycle: 2 }).state).toBe('decided');
    expect(toCard({ ...wire, lifecycle: 3 }).state).toBe('archived');
    expect(toCard({ ...wire, lifecycle: 0 }).state).toBe('active');
  });

  it('also accepts string enums so a future JsonStringEnumConverter cannot break it', () => {
    expect(toCard({ ...wire, type: 'dashboard', lifecycle: 'archived' }).type).toBe('dashboard');
    expect(toCard({ ...wire, type: 'dashboard', lifecycle: 'archived' }).state).toBe('archived');
  });

  it('falls back to a valid style key for an unknown enum rather than crashing', () => {
    const card = toCard({ ...wire, type: 99, lifecycle: 99 });
    expect(card.type).toBe('voting');
    expect(card.state).toBe('active');
  });

  it('maps council health ratings onto the vote choices the panel renders', () => {
    const card = toCard(wire);
    expect(card.votes?.find(v => v.userId === 'supply-chain')?.choice).toBe('reject');
    expect(card.votes?.find(v => v.userId === 'demand-forecasting')?.choice).toBe('approve');
  });

  it('renames the vote timestamp field the panel reads', () => {
    expect(toCard(wire).votes?.[0].votedAt).toBe('2026-08-28T02:40:50Z');
  });

  it('gives comments a stable id, because the server assigns none', () => {
    const ids = toCard(wire).comments?.map(c => c.id);
    expect(ids?.[0]).toBeTruthy();
    expect(toCard(wire).comments?.[0].id).toBe(ids?.[0]);
  });

  it('surfaces the verdict synthesis as the card summary', () => {
    expect(toCard(wire).summary).toBe('Supply reliability is the binding constraint.');
  });

  it('falls back to brand and rating when there is no synthesis', () => {
    const card = toCard({ ...wire, data: { brand: 'FreshMart', overall_rating: 'Green' } });
    expect(card.summary).toBe('FreshMart assessed as Green.');
  });

  it('flags escalation only when the server supplied a reason', () => {
    expect(toCard(wire).escalated).toBe(false);
    expect(toCard({ ...wire, escalationReason: 'Split vote' }).escalated).toBe(true);
  });

  it('survives a sparse payload without throwing', () => {
    const card = toCard({});
    expect(card.type).toBe('voting');
    expect(card.state).toBe('active');
    expect(card.votes).toEqual([]);
    expect(card.comments).toEqual([]);
  });
});
