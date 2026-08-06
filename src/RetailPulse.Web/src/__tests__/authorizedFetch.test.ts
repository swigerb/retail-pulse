import { describe, it, expect, vi, beforeEach } from 'vitest';

// The wrapper only installs when auth is configured; force that in tests.
vi.mock('../auth/authConfig', () => ({ authConfig: { isConfigured: true } }));

// Central token acquisition is mocked so we can drive silent-refresh / retry behavior.
const acquireApiToken = vi.fn();
vi.mock('../auth/tokenService', () => ({
  acquireApiToken: (...args: unknown[]) => acquireApiToken(...args),
}));

type FetchModule = typeof import('../auth/authorizedFetch');

async function freshModule(): Promise<FetchModule> {
  vi.resetModules(); // resets the module-level `installed` guard so each test wraps cleanly
  return import('../auth/authorizedFetch');
}

function jsonResponse(status: number): Response {
  return new Response('{}', { status, headers: { 'content-type': 'application/json' } });
}

let originalFetch: ReturnType<typeof vi.fn>;

beforeEach(() => {
  acquireApiToken.mockReset();
  originalFetch = vi.fn().mockResolvedValue(jsonResponse(200));
  window.fetch = originalFetch as unknown as typeof window.fetch;
});

function authHeader(callIndex = 0): string | null {
  const init = originalFetch.mock.calls[callIndex]?.[1] as RequestInit | undefined;
  const headers = new Headers(init?.headers);
  return headers.get('Authorization');
}

describe('isApiRequest', () => {
  it('matches relative /api and /hubs but not third-party origins', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest('/api/chat')).toBe(true);
    expect(isApiRequest('/hubs/telemetry')).toBe(true);
    expect(isApiRequest('https://cdn.example.com/lib.js')).toBe(false);
  });
});

describe('installAuthorizedFetch', () => {
  it('attaches the bearer token to API requests', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/api/chat', { method: 'POST' });

    expect(acquireApiToken).toHaveBeenCalledTimes(1);
    expect(authHeader()).toBe('Bearer tok-1');
  });

  it('does not touch non-API requests or acquire a token for them', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('https://cdn.example.com/lib.js');

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader()).toBeNull();
  });

  it('force-refreshes the token and retries once on a 401', async () => {
    const { installAuthorizedFetch } = await freshModule();
    originalFetch
      .mockResolvedValueOnce(jsonResponse(401))
      .mockResolvedValueOnce(jsonResponse(200));
    acquireApiToken
      .mockResolvedValueOnce('stale') // initial
      .mockResolvedValueOnce('fresh'); // forced refresh on retry
    installAuthorizedFetch();

    const res = await window.fetch('/api/chat');

    expect(res.status).toBe(200);
    expect(originalFetch).toHaveBeenCalledTimes(2);
    expect(authHeader(0)).toBe('Bearer stale');
    expect(authHeader(1)).toBe('Bearer fresh');
    expect(acquireApiToken).toHaveBeenLastCalledWith({ forceRefresh: true });
  });

  it('emits an auth-required event when a 401 persists after retry', async () => {
    const { installAuthorizedFetch, AUTH_REQUIRED_EVENT } = await freshModule();
    originalFetch.mockResolvedValue(jsonResponse(401));
    acquireApiToken.mockResolvedValue('tok');
    installAuthorizedFetch();

    const handler = vi.fn();
    window.addEventListener(AUTH_REQUIRED_EVENT, handler);
    await window.fetch('/api/chat');
    window.removeEventListener(AUTH_REQUIRED_EVENT, handler);

    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('emits an auth-forbidden event on a 403 (missing role/scope)', async () => {
    const { installAuthorizedFetch, AUTH_FORBIDDEN_EVENT } = await freshModule();
    originalFetch.mockResolvedValue(jsonResponse(403));
    acquireApiToken.mockResolvedValue('tok');
    installAuthorizedFetch();

    const handler = vi.fn();
    window.addEventListener(AUTH_FORBIDDEN_EVENT, handler);
    await window.fetch('/api/observability/costs');
    window.removeEventListener(AUTH_FORBIDDEN_EVENT, handler);

    expect(handler).toHaveBeenCalledTimes(1);
  });
});
