import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { sendMessage } from '../services/api';

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

    await sendMessage({ message: 'ping', sessionId: 'abc' });

    expect(captured?.body).toBe(JSON.stringify({ message: 'ping', sessionId: 'abc' }));
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
});
