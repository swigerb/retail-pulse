import { requiresGate } from './activeProvider';
import { acquireApiToken } from './tokenService';
import { resolveApiOrigin } from '../config/apiOrigin';

/**
 * Global fetch interceptor that attaches the ACTIVE provider's session bearer token to our
 * protected `/api` REST requests only.
 *
 * Rather than editing ~16 service modules (and risking a future call that forgets the
 * header), token attachment is centralized here: {@link installAuthorizedFetch} wraps
 * `window.fetch` once at startup so ALL protected/billable REST goes out authenticated with
 * whatever provider is configured (Entra MSAL, GitHub BFF session, or Anonymous session).
 * The token is scoped to same-origin `/api` paths (see {@link isApiRequest}); SignalR
 * (`/hubs`) handshakes carry the token via their own `accessTokenFactory` (see
 * tokenService) and are intentionally excluded, as are assets, third parties, and
 * App Insights. A no-op when no provider gate is active (local dev pass-through).
 *
 * On a 401 the wrapper transparently forces a fresh token and retries once; if it still
 * fails it emits {@link AUTH_REQUIRED_EVENT} so the sign-in gate can re-authenticate. A 403
 * (authenticated but missing the RetailPulse.User role/scope) emits
 * {@link AUTH_FORBIDDEN_EVENT} so the UI can show a precise access-denied message.
 */

export const AUTH_REQUIRED_EVENT = 'retailpulse:auth-required';
export const AUTH_FORBIDDEN_EVENT = 'retailpulse:auth-forbidden';

/**
 * Protected REST surface the bearer token may be attached to. This is our own API only —
 * SignalR (`/hubs`) is deliberately EXCLUDED because it carries the token via its own
 * `accessTokenFactory` (see tokenService), and anything outside `/api` (static assets,
 * App Insights, third parties) must never receive the token.
 */
const PROTECTED_API_PREFIX = '/api';

function currentOrigin(): string | null {
  if (typeof window !== 'undefined' && window.location && window.location.origin) {
    return window.location.origin;
  }
  return null;
}

function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.toString();
  return input.url;
}

/** True only for our own `/api` protected paths on an explicitly trusted origin. */
function isProtectedApiPath(pathname: string): boolean {
  return pathname === PROTECTED_API_PREFIX || pathname.startsWith(`${PROTECTED_API_PREFIX}/`);
}

/**
 * Decides whether a request targets our protected API surface and may therefore carry the
 * Entra bearer token. The check is deliberately strict to avoid ever leaking the token to a
 * lookalike, a third party, or a same-origin non-API path:
 *   1. Resolve the URL against the current origin (`new URL(input, location.origin)`), so
 *      relative and absolute inputs are normalized identically. Unparseable URLs are rejected.
 *   2. Reject any URL that embeds credentials (userinfo), e.g. `https://trusted@evil.example`.
 *   3. Require an EXACT origin match (scheme + host + port) — no suffix/prefix lookalikes,
 *      no scheme or port mismatches, no cross-origin App Insights ingestion endpoints.
 *   4. Require the path to be on the `/api` allowlist. `/hubs` (SignalR) is handled
 *      separately by its own accessTokenFactory and is intentionally not matched here.
 */
export function isApiRequest(input: RequestInfo | URL): boolean {
  const origin = currentOrigin();
  if (!origin) {
    return false;
  }

  let parsed: URL;
  try {
    parsed = new URL(requestUrl(input), origin);
  } catch {
    return false;
  }

  // Never attach the token to a URL that smuggles credentials in the userinfo section.
  if (parsed.username !== '' || parsed.password !== '') {
    return false;
  }

  // Exact-origin only: the SWA origin and the configured ACA API origin. This
  // rejects trusted@evil, suffix hosts, scheme/port mismatches, and telemetry
  // endpoints while allowing long-running chat to bypass the SWA proxy timeout.
  const configuredApiOrigin = resolveApiOrigin(import.meta.env.VITE_API_ORIGIN);
  if (parsed.origin !== origin && parsed.origin !== configuredApiOrigin) {
    return false;
  }

  // Only our own /api protected paths. Encoded traversal (%2e%2e) and normalized `..`
  // that escape /api resolve to a non-/api pathname and are excluded.
  return isProtectedApiPath(parsed.pathname);
}

let installed = false;

export function installAuthorizedFetch(): void {
  if (installed || !requiresGate || typeof window === 'undefined') {
    return;
  }
  installed = true;

  const originalFetch: typeof window.fetch = window.fetch.bind(window);

  const send = (input: RequestInfo | URL, init: RequestInit | undefined, bearer: string | null) => {
    if (!bearer) {
      return originalFetch(input, init);
    }
    if (input instanceof Request) {
      const headers = new Headers(input.headers);
      headers.set('Authorization', `Bearer ${bearer}`);
      return originalFetch(new Request(input, { headers }));
    }
    const headers = new Headers(init?.headers);
    headers.set('Authorization', `Bearer ${bearer}`);
    return originalFetch(input, { ...init, headers });
  };

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    if (!isApiRequest(input)) {
      return originalFetch(input, init);
    }

    let token: string | null;
    try {
      token = await acquireApiToken();
    } catch {
      token = null; // fall through unauthenticated; the 401 path below handles recovery
    }

    let response = await send(input, init, token);

    if (response.status === 401) {
      let fresh: string | null;
      try {
        fresh = await acquireApiToken({ forceRefresh: true });
      } catch {
        fresh = null;
      }
      if (fresh) {
        response = await send(input, init, fresh);
      }
      if (response.status === 401) {
        window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
      }
    } else if (response.status === 403) {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    }

    return response;
  };
}
