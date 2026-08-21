import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { sendMessage, isErrorReply } from '../services/api';

const originalFetch = globalThis.fetch;

describe('api.sendMessage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllEnvs();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('parses a successful response', async () => {
    const payload = {
      reply: 'hello',
      sessionId: 's-1',
      spans: [],
      totalDurationMs: 1234,
    };
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(payload), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    ) as unknown as typeof fetch;

    const result = await sendMessage({ message: 'hi' });

    expect(result.kind).toBe('complete');
    if (result.kind !== 'complete') throw new Error('expected complete');
    expect(result.response.reply).toBe('hello');
    expect(result.response.sessionId).toBe('s-1');
    expect(result.response.totalDurationMs).toBe(1234);
    expect(globalThis.fetch).toHaveBeenCalledWith(
      '/api/chat',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
      })
    );
  });

  it('serializes the request body as JSON', async () => {
    let captured: RequestInit | undefined;
    globalThis.fetch = vi.fn().mockImplementation(
      (_input: unknown, init?: RequestInit) => {
        captured = init;
        return Promise.resolve(
          new Response(JSON.stringify({ reply: 'x', sessionId: 'y', spans: [] }), { status: 200 })
        );
      }
    ) as unknown as typeof fetch;

    await sendMessage({
      message: 'ping',
      sessionId: 'abc',
      history: [
        { role: 'user', content: 'previous question' },
        { role: 'assistant', content: 'previous answer' },
      ],
    });

    expect(captured?.body).toBe(JSON.stringify({
      message: 'ping',
      sessionId: 'abc',
      history: [
        { role: 'user', content: 'previous question' },
        { role: 'assistant', content: 'previous answer' },
      ],
    }));
  });

  it('omits forceExecutionPath from the body when not provided (issue #95 Auto default)', async () => {
    let captured: RequestInit | undefined;
    globalThis.fetch = vi.fn().mockImplementation(
      (_input: unknown, init?: RequestInit) => {
        captured = init;
        return Promise.resolve(
          new Response(JSON.stringify({ reply: 'ok', sessionId: 's', spans: [] }), { status: 200 })
        );
      }
    ) as unknown as typeof fetch;

    await sendMessage({ message: 'hi', sessionId: 's' });

    // Auto (the default) never sends the field so the payload is
    // byte-identical to pre-#95 requests. This preserves today's fast-path
    // UX for the common case.
    expect(captured?.body).toBe(JSON.stringify({ message: 'hi', sessionId: 's' }));
    expect((captured?.body as string) ?? '').not.toContain('forceExecutionPath');
  });

  it.each([
    ['fast' as const],
    ['plan' as const],
  ])('serializes forceExecutionPath=%s when the caller forces the path', async (path) => {
    let captured: RequestInit | undefined;
    globalThis.fetch = vi.fn().mockImplementation(
      (_input: unknown, init?: RequestInit) => {
        captured = init;
        return Promise.resolve(
          new Response(JSON.stringify({ reply: 'ok', sessionId: 's', spans: [] }), { status: 200 })
        );
      }
    ) as unknown as typeof fetch;

    await sendMessage({ message: 'hi', sessionId: 's', forceExecutionPath: path });

    expect(captured?.body).toBe(
      JSON.stringify({ message: 'hi', sessionId: 's', forceExecutionPath: path }),
    );
  });

  it('parses executionPath + executionPathForced on the routing payload (issue #95)', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          reply: 'ok',
          sessionId: 's',
          spans: [],
          routing: {
            agentKey: 'demand-forecasting',
            agentName: 'Demand Agent',
            intent: 'demand/forecasting',
            confidence: 0.91,
            executionPath: 'plan',
            executionPathForced: true,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ) as unknown as typeof fetch;

    const result = await sendMessage({ message: 'hi' });

    if (result.kind !== 'complete') throw new Error('expected complete');
    expect(result.response.routing?.executionPath).toBe('plan');
    expect(result.response.routing?.executionPathForced).toBe(true);
  });

  it('returns a suspended envelope when the plan review gate returns 202 (issue #96)', async () => {
    const payload = {
      planId: 'plan-42',
      status: 'awaiting_review',
      reviewRequestId: 'req-1',
      round: 0,
      sessionId: 's-99',
      message: 'Plan awaiting reviewer input.',
    };
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(payload), {
        status: 202,
        headers: { 'Content-Type': 'application/json' },
      }),
    ) as unknown as typeof fetch;

    const result = await sendMessage({ message: 'multi-domain question' });

    expect(result.kind).toBe('suspended');
    if (result.kind !== 'suspended') throw new Error('expected suspended');
    expect(result.suspended.planId).toBe('plan-42');
    expect(result.suspended.status).toBe('awaiting_review');
    expect(result.suspended.reviewRequestId).toBe('req-1');
  });

  it('throws when a 202 response body is malformed', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ planId: 'x' }), {
        status: 202,
        headers: { 'Content-Type': 'application/json' },
      }),
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/malformed/i);
  });

  it('rejects a routing payload with an unknown executionPath value', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          reply: 'ok',
          sessionId: 's',
          spans: [],
          routing: {
            agentKey: 'x',
            agentName: 'x',
            intent: 'demand/x',
            confidence: 0.9,
            executionPath: 'nope',
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/malformed/i);
  });

  it('uses the configured direct ACA origin for long-running chat', async () => {
    vi.stubEnv('VITE_API_ORIGIN', 'https://api.example.test');
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ reply: 'ok', sessionId: 's', spans: [] }), { status: 200 })
    ) as unknown as typeof fetch;

    await sendMessage({ message: 'compare two brands' });

    expect(globalThis.fetch).toHaveBeenCalledWith(
      'https://api.example.test/api/chat',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('throws an Error containing the status when response is non-2xx', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('boom', { status: 500 })
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/500/);
  });

  it('throws when response is 4xx', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('bad', { status: 400 })
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/400/);
  });

  it('propagates network failures from fetch', async () => {
    globalThis.fetch = vi.fn().mockRejectedValue(
      new TypeError('Failed to fetch')
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/Failed to fetch/);
  });

  it('throws a friendly timeout error when the request exceeds timeoutMs', async () => {
    vi.useFakeTimers();
    try {
      globalThis.fetch = vi.fn().mockImplementation(
        (_input: unknown, init?: RequestInit) =>
          new Promise((_resolve, reject) => {
            init?.signal?.addEventListener('abort', () => {
              reject(new DOMException('Aborted', 'AbortError'));
            });
          })
      ) as unknown as typeof fetch;

      const promise = sendMessage({ message: 'hi' }, { timeoutMs: 50 });
      const assertion = expect(promise).rejects.toThrow(/timed out/i);
      await vi.advanceTimersByTimeAsync(60);
      await assertion;
    } finally {
      vi.useRealTimers();
    }
  });

  it('respects a caller-provided AbortSignal', async () => {
    const controller = new AbortController();
    globalThis.fetch = vi.fn().mockImplementation(
      (_input: unknown, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            reject(new DOMException('Aborted', 'AbortError'));
          });
        })
    ) as unknown as typeof fetch;

    const promise = sendMessage({ message: 'hi' }, { signal: controller.signal });
    controller.abort();
    await expect(promise).rejects.toThrow(/Aborted|abort/i);
  });

  it('throws a friendly message for 429 rate-limit responses', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('Too Many Requests', { status: 429 })
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(
      'The AI service is busy. Please wait a moment and try again.'
    );
  });

  it('does not use the friendly 429 message for other 4xx errors', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('bad', { status: 400 })
    ) as unknown as typeof fetch;

    await expect(sendMessage({ message: 'hi' })).rejects.toThrow(/API error 400/);
  });
});

describe('isErrorReply', () => {
  it('returns true for backend error replies starting with ⏳', () => {
    expect(isErrorReply('⏳ The AI service is experiencing high demand. Please wait 30 seconds and try again.')).toBe(true);
    expect(isErrorReply('⏳ The request took too long to complete.')).toBe(true);
  });

  it('returns false for normal replies', () => {
    expect(isErrorReply('Here is the demand forecast for Q2.')).toBe(false);
    expect(isErrorReply('')).toBe(false);
  });
});
