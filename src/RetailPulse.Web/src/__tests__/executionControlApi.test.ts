import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  cancelChatSession,
  cancelPlan,
  reconcilePlan,
} from '../services/executionControlApi';

const originalFetch = globalThis.fetch;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('executionControlApi', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  describe('cancelChatSession', () => {
    it('POSTs to /api/chat/{sessionId}/cancel with a JSON content type', async () => {
      const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      const result = await cancelChatSession('sess-123');

      expect(result).toBe('cancelled');
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/chat/sess-123/cancel',
        expect.objectContaining({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });

    it('URL-encodes the sessionId to prevent path injection', async () => {
      const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      await cancelChatSession('has/slash?and=query');

      const url = fetchMock.mock.calls[0][0] as string;
      expect(url).toBe('/api/chat/has%2Fslash%3Fand%3Dquery/cancel');
    });

    it('maps 404 to not_found (no in-flight run for this caller)', async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 })) as unknown as typeof fetch;
      await expect(cancelChatSession('sess-x')).resolves.toBe('not_found');
    });

    it('throws on unexpected status codes', async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 500 })) as unknown as typeof fetch;
      await expect(cancelChatSession('sess-x')).rejects.toThrow(/HTTP 500/);
    });

    it('rejects an empty sessionId synchronously', () => {
      expect(() => cancelChatSession('')).toThrow(/non-empty sessionId/);
    });
  });

  describe('cancelPlan', () => {
    it('maps 403 to forbidden so the UI can hide the affordance', async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 403 })) as unknown as typeof fetch;
      await expect(cancelPlan('plan-1')).resolves.toBe('forbidden');
    });

    it('POSTs to /api/plans/{planId}/cancel', async () => {
      const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      await cancelPlan('plan-abc');

      const url = fetchMock.mock.calls[0][0] as string;
      expect(url).toBe('/api/plans/plan-abc/cancel');
    });
  });

  describe('reconcilePlan', () => {
    it('includes afterStepIndex when supplied and parses the response', async () => {
      const payload = {
        planId: 'plan-1',
        sessionId: 'sess-1',
        status: 'running',
        totalStepCount: 5,
        afterStepIndex: 2,
        steps: [
          { stepIndex: 3, status: 'succeeded' },
          { stepIndex: 4, status: 'running' },
        ],
      };
      const fetchMock = vi.fn().mockResolvedValue(jsonResponse(payload));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      const result = await reconcilePlan('plan-1', { afterStepIndex: 2 });

      expect(fetchMock).toHaveBeenCalledWith(
        '/api/plans/plan-1/reconcile?afterStepIndex=2',
        expect.objectContaining({ method: 'GET' }),
      );
      expect(result?.steps).toHaveLength(2);
      expect(result?.status).toBe('running');
    });

    it('omits afterStepIndex from the query string when not supplied', async () => {
      const payload = {
        planId: 'plan-1',
        sessionId: 'sess-1',
        status: 'running',
        totalStepCount: 0,
        afterStepIndex: -1,
        steps: [],
      };
      const fetchMock = vi.fn().mockResolvedValue(jsonResponse(payload));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      await reconcilePlan('plan-1');

      expect(fetchMock.mock.calls[0][0]).toBe('/api/plans/plan-1/reconcile');
    });

    it('returns null when the plan is unknown or foreign (404 / 403)', async () => {
      globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 404 })) as unknown as typeof fetch;
      await expect(reconcilePlan('missing')).resolves.toBeNull();

      globalThis.fetch = vi.fn().mockResolvedValue(new Response(null, { status: 403 })) as unknown as typeof fetch;
      await expect(reconcilePlan('missing')).resolves.toBeNull();
    });

    it('rejects a malformed payload rather than propagating unknown shape', async () => {
      const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ nope: true }));
      globalThis.fetch = fetchMock as unknown as typeof fetch;

      await expect(reconcilePlan('plan-1')).rejects.toThrow(/malformed payload/);
    });
  });
});
