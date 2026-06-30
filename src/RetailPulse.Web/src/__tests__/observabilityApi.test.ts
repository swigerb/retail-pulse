import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fetchCostDashboard, fetchExportSessions } from '../services/observabilityApi';

const originalFetch = globalThis.fetch;

describe('observabilityApi.fetchExportSessions', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('normalizes sessions with missing backend fields', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify([
        null,
        'not-a-session',
        {
          sessionId: 'session-missing-fields',
          messageCount: 3,
        },
      ]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    ) as unknown as typeof fetch;

    const sessions = await fetchExportSessions();

    expect(sessions).toEqual([
      {
        sessionId: 'session-missing-fields',
        startTime: '',
        messageCount: 3,
        agentsUsed: [],
        totalTokens: 0,
      },
    ]);
  });
});

describe('observabilityApi.fetchCostDashboard', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('fans out to cost endpoints and maps backend shapes', async () => {
    globalThis.fetch = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/observability/costs?period=week') {
        return Promise.resolve(new Response(JSON.stringify({
          totalTokens: 1000,
          totalCost: 2,
          requestCount: 4,
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      if (url === '/api/observability/costs/agents?period=week') {
        return Promise.resolve(new Response(JSON.stringify([
          { agentId: 'chick', tokens: 600, cost: 1.25, requests: 3, topTool: 'chart' },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      if (url === '/api/observability/costs/trend?days=7') {
        return Promise.resolve(new Response(JSON.stringify({
          days: [{ date: '2026-06-30T00:00:00Z', cost: 0.5, tokens: 250 }],
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      if (url === '/api/observability/costs/tools?period=week') {
        return Promise.resolve(new Response(JSON.stringify([
          { toolName: 'SearchInventory', callCount: 5, totalTokens: 300, avgDurationMs: 42 },
        ]), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      return Promise.resolve(new Response(null, { status: 404 }));
    }) as unknown as typeof fetch;

    const dashboard = await fetchCostDashboard('week');

    expect(globalThis.fetch).toHaveBeenCalledTimes(4);
    expect(dashboard).toEqual({
      summary: {
        totalTokens: 1000,
        totalCost: 2,
        requestCount: 4,
        avgCostPerRequest: 0.5,
      },
      trend: [{
        date: new Date('2026-06-30T00:00:00Z').toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
        cost: 0.5,
        tokens: 250,
      }],
      agentBreakdown: [{
        agentName: 'chick',
        totalCost: 1.25,
        totalTokens: 600,
        requestCount: 3,
      }],
      topTools: [{
        toolName: 'SearchInventory',
        callCount: 5,
        totalTokens: 300,
        avgDurationMs: 42,
      }],
    });
  });

  it('keeps secondary endpoint failures isolated', async () => {
    globalThis.fetch = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/observability/costs?period=today') {
        return Promise.resolve(new Response(JSON.stringify({
          totalTokens: 10,
          totalCost: 0.2,
          requestCount: 2,
        }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }
      return Promise.resolve(new Response(null, { status: 500 }));
    }) as unknown as typeof fetch;

    const dashboard = await fetchCostDashboard('today');

    expect(dashboard.summary.avgCostPerRequest).toBe(0.1);
    expect(dashboard.trend).toEqual([]);
    expect(dashboard.agentBreakdown).toEqual([]);
    expect(dashboard.topTools).toEqual([]);
  });
});
