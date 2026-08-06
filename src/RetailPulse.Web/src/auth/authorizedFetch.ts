import { authConfig } from './authConfig';
import { acquireApiToken } from './tokenService';

/**
 * Global fetch interceptor that attaches the Entra bearer token to every API/hub request.
 *
 * Rather than editing ~16 service modules (and risking a future call that forgets the
 * header), token attachment is centralized here: {@link installAuthorizedFetch} wraps
 * `window.fetch` once at startup so ALL protected/billable REST goes out authenticated.
 * SignalR handshakes use their own `accessTokenFactory` (see tokenService), so this only
 * needs to cover `fetch`. A no-op when auth is unconfigured (local dev).
 *
 * On a 401 the wrapper transparently forces a fresh token and retries once; if it still
 * fails it emits {@link AUTH_REQUIRED_EVENT} so the sign-in gate can re-authenticate. A 403
 * (authenticated but missing the RetailPulse.User role/scope) emits
 * {@link AUTH_FORBIDDEN_EVENT} so the UI can show a precise access-denied message.
 */

export const AUTH_REQUIRED_EVENT = 'retailpulse:auth-required';
export const AUTH_FORBIDDEN_EVENT = 'retailpulse:auth-forbidden';

const API_ORIGIN = (import.meta.env.VITE_API_ORIGIN ?? '').replace(/\/+$/, '');

/** True when the URL targets our API/hub surface (relative /api, /hubs, or the ACA origin). */
export function isApiRequest(url: string): boolean {
  if (url.startsWith('/api') || url.startsWith('/hubs')) {
    return true;
  }
  if (API_ORIGIN && url.startsWith(API_ORIGIN)) {
    return true;
  }
  return false;
}

function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.toString();
  return input.url;
}

let installed = false;

export function installAuthorizedFetch(): void {
  if (installed || !authConfig.isConfigured || typeof window === 'undefined') {
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
    if (!isApiRequest(requestUrl(input))) {
      return originalFetch(input, init);
    }

    let token: string | null = null;
    try {
      token = await acquireApiToken();
    } catch {
      token = null; // fall through unauthenticated; the 401 path below handles recovery
    }

    let response = await send(input, init, token);

    if (response.status === 401) {
      let fresh: string | null = null;
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
