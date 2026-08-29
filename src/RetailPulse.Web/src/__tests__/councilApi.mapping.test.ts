import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchCouncilHistory, toCouncilSession } from '../services/councilApi';

describe('councilApi wire mapping', () => {
  // Captured from the GET /api/council/history response shape in ChatEndpoints.
  const wire = {
    id: 'Summit Vodka-2026-08-28T02:40:50.2235061Z',
    brand: 'Summit Vodka',
    region: 'All Regions',
    convened_at: '2026-08-28T02:40:50.2235061Z',
    overall_rating: 'Red',
    synthesis: 'Supply reliability is the binding constraint.',
    is_unanimous: false,
    disagreements: [
      'Supply Chain rated Red, while Demand Forecasting rated Green.',
    ],
    action_items: [
      '1. Immediately restore Northeast allocation.',
      '2. Monitor out-of-stock risk.',
    ],
    total_duration_ms: 4200,
    votes: [
      {
        agent_id: 'supply-chain',
        agent_name: 'Supply Chain',
        rating: 'Red',
        reasoning: 'Northeast allocation is below the operating floor.',
        confidence: 0.82,
        key_metrics: ['Fill Rate 78%', 'Inventory Weeks 1.1'],
        response_time_ms: 940,
      },
      {
        agent_id: 'demand-forecasting',
        agent_name: 'Demand Forecasting',
        rating: 'Green',
        reasoning: 'Demand is stable.',
        confidence: 0.91,
        key_metrics: ['Forecast Accuracy 94%'],
        response_time_ms: 880,
      },
    ],
  };

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('nests the flat history payload into the panel view model', () => {
    const session = toCouncilSession(wire);

    expect(session.id).toBe(wire.id);
    expect(session.convenedAt).toBe(wire.convened_at);
    expect(session.verdict.overallRating).toBe('red');
    expect(session.verdict.synthesisText).toBe(wire.synthesis);
    expect(session.verdict.unanimous).toBe(false);
    expect(session.verdict.totalConveneTimeMs).toBe(4200);
  });

  it('maps votes and action items into the fields the panel renders', () => {
    const session = toCouncilSession(wire);

    expect(session.votes[0]).toMatchObject({
      agentId: 'supply-chain',
      agentName: 'Supply Chain',
      domain: 'supply',
      rating: 'red',
      keyMetrics: ['Fill Rate 78%', 'Inventory Weeks 1.1'],
      responseTimeMs: 940,
    });
    expect(session.verdict.actionItems[0]).toEqual({
      priority: 1,
      text: 'Immediately restore Northeast allocation.',
    });
    expect(session.verdict.disagreements[0].topic).toBe(wire.disagreements[0]);
  });

  it('also accepts integer rating enums so serializer changes do not break styles', () => {
    const session = toCouncilSession({
      ...wire,
      overall_rating: 2,
      votes: [{ ...wire.votes[0], rating: 0 }],
    });

    expect(session.verdict.overallRating).toBe('red');
    expect(session.votes[0].rating).toBe('green');
  });

  it('maps fetchCouncilHistory at the API boundary', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [wire],
    }));

    await expect(fetchCouncilHistory()).resolves.toMatchObject([
      {
        brand: 'Summit Vodka',
        verdict: {
          overallRating: 'red',
          actionItems: [
            { priority: 1, text: 'Immediately restore Northeast allocation.' },
            { priority: 2, text: 'Monitor out-of-stock risk.' },
          ],
        },
      },
    ]);
  });

  it('turns a malformed row into readable defaults rather than throwing', () => {
    expect(() => toCouncilSession({ id: 'bad-row', votes: null, action_items: null })).not.toThrow();

    const session = toCouncilSession({ id: 'bad-row', votes: null, action_items: null });
    expect(session.brand).toBe('Unknown brand');
    expect(session.verdict.overallRating).toBe('yellow');
    expect(session.verdict.synthesisText).toBe('No synthesis is available for this council session.');
    expect(session.votes).toEqual([]);
    expect(session.verdict.actionItems).toEqual([]);
  });
});
