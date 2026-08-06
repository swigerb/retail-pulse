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
  it('matches relative /api paths only', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest('/api/chat')).toBe(true);
    expect(isApiRequest('/api')).toBe(true);
    expect(isApiRequest('/api/observability/costs')).toBe(true);
  });

  it('does not match SignalR /hubs (handled by accessTokenFactory)', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest('/hubs/telemetry')).toBe(false);
    expect(isApiRequest('/hubs/telemetry/negotiate')).toBe(false);
    expect(isApiRequest(`${location.origin}/hubs/telemetry`)).toBe(false);
  });

  it('does not match assets, third parties, or App Insights', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest('https://cdn.example.com/lib.js')).toBe(false);
    expect(isApiRequest('/assets/app.js')).toBe(false);
    expect(isApiRequest('/favicon.ico')).toBe(false);
    expect(isApiRequest('https://dc.services.visualstudio.com/v2/track')).toBe(false);
  });

  it('rejects userinfo credential smuggling (trusted@evil)', async () => {
    const { isApiRequest } = await freshModule();
    // Host is evil.example; the current origin is only in the username section.
    expect(isApiRequest(`https://${location.hostname}@evil.example/api/chat`)).toBe(false);
    expect(isApiRequest('https://user:pass@evil.example/api/chat')).toBe(false);
  });

  it('rejects lookalike suffix/prefix hosts', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest(`https://${location.hostname}.evil.example/api/chat`)).toBe(false);
    expect(isApiRequest(`https://evil${location.hostname}/api/chat`)).toBe(false);
  });

  it('rejects scheme and port mismatches for the same host', async () => {
    const { isApiRequest } = await freshModule();
    // jsdom origin is http://localhost:3000 — flip scheme and port.
    expect(isApiRequest(`https://${location.host}/api/chat`)).toBe(false);
    expect(isApiRequest(`${location.protocol}//${location.hostname}:5999/api/chat`)).toBe(false);
  });

  it('matches an absolute same-origin /api URL', async () => {
    const { isApiRequest } = await freshModule();
    expect(isApiRequest(`${location.origin}/api/chat`)).toBe(true);
  });

  it('does not match encoded traversal or paths that escape /api', async () => {
    const { isApiRequest } = await freshModule();
    // `..` (raw or percent-encoded %2e%2e) is normalized by the URL parser. When it escapes
    // /api the pathname is no longer under /api, so no token is attached.
    expect(isApiRequest('/api/../secret')).toBe(false);
    expect(isApiRequest('/%2e%2e/secret')).toBe(false);
    expect(isApiRequest('/apidocs/public')).toBe(false);
    // Encoded traversal that normalizes BACK into /api is still a genuine same-origin API
    // path, so authorizing it is correct (the server resolves it to /api/secret too).
    expect(isApiRequest('/%2e%2e/api/secret')).toBe(true);
  });

  it('accepts Request objects and URL objects for /api', async () => {
    const { isApiRequest } = await freshModule();
    // Browsers expose an absolute Request.url; jsdom's Request requires an absolute input.
    expect(isApiRequest(new Request(`${location.origin}/api/chat`))).toBe(true);
    expect(isApiRequest(new URL('/api/chat', location.origin))).toBe(true);
    expect(isApiRequest(new Request('https://cdn.example.com/lib.js'))).toBe(false);
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

  it('does not attach the token to same-origin non-API paths', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/assets/app.js');
    await window.fetch(`${location.origin}/index.html`);

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader(0)).toBeNull();
  });

  it('does not attach the token to /hubs (SignalR carries its own token)', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch('/hubs/telemetry/negotiate', { method: 'POST' });

    expect(acquireApiToken).not.toHaveBeenCalled();
    expect(authHeader()).toBeNull();
  });

  it('attaches the bearer to a Request object targeting /api and retries it on 401', async () => {
    const { installAuthorizedFetch } = await freshModule();
    originalFetch
      .mockResolvedValueOnce(jsonResponse(401))
      .mockResolvedValueOnce(jsonResponse(200));
    acquireApiToken
      .mockResolvedValueOnce('stale')
      .mockResolvedValueOnce('fresh');
    installAuthorizedFetch();

    const res = await window.fetch(new Request(`${location.origin}/api/chat`, { method: 'POST' }));

    expect(res.status).toBe(200);
    expect(originalFetch).toHaveBeenCalledTimes(2);
    // Request objects are re-wrapped, so the first arg is a Request carrying the header.
    const first = originalFetch.mock.calls[0]?.[0] as Request;
    expect(first).toBeInstanceOf(Request);
    expect(first.headers.get('Authorization')?.endsWith('stale')).toBe(true);
    // The retry re-wraps the same Request with the freshly refreshed token.
    const second = originalFetch.mock.calls[1]?.[0] as Request;
    expect(second).toBeInstanceOf(Request);
    expect(second.headers.get('Authorization')?.endsWith('fresh')).toBe(true);
  });

  it('does not acquire a token for a userinfo lookalike host', async () => {
    const { installAuthorizedFetch } = await freshModule();
    acquireApiToken.mockResolvedValue('tok-1');
    installAuthorizedFetch();

    await window.fetch(`https://${location.hostname}@evil.example/api/chat`);

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
