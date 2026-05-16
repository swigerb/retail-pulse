import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { sendMessage, isErrorReply } from '../services/api';

const originalFetch = globalThis.fetch;

describe('api.sendMessage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
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

    expect(result.reply).toBe('hello');
    expect(result.sessionId).toBe('s-1');
    expect(result.totalDurationMs).toBe(1234);
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
    expect(isErrorReply('⏳ The AI service is temporarily rate-limited. Please wait a moment and try again.')).toBe(true);
    expect(isErrorReply('⏳ The request took too long to complete.')).toBe(true);
  });

  it('returns false for normal replies', () => {
    expect(isErrorReply('Here is the demand forecast for Q2.')).toBe(false);
    expect(isErrorReply('')).toBe(false);
  });
});
