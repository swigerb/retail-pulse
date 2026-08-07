import { resolveApiUrl } from '../../config/apiOrigin';
import { SessionCredentialStore } from '../session/sessionCredentialStore';
import {
  FULL_CAPABILITIES,
  type ProviderCapabilities,
  type SessionProvider,
  type SessionState,
} from '../session/types';

/**
 * GitHub confidential Backend-for-Frontend (BFF) session provider (opt-in, non-production).
 *
 * The GitHub PROVIDER token never touches the browser. The flow the SPA drives is:
 *   1. {@link startLogin} performs a TOP-LEVEL navigation to `GET /api/auth/github/start` (never an
 *      arbitrary/user-supplied return URL) — the backend sets its browser-bound state cookie and
 *      redirects to github.com.
 *   2. github.com redirects back to the backend callback, which redirects to THIS SPA carrying only
 *      a one-time redemption `code` (or a safe `error` code) in the query string.
 *   3. {@link initialize} runs at load: it reads `code`/`error`, IMMEDIATELY strips them from the URL
 *      with `history.replaceState` (so a reload/bookmark/back can never replay them), and — for a
 *      `code` — POSTs `POST /api/auth/github/exchange` to redeem it for a short-lived Retail Pulse
 *      session token, stored session-only.
 *
 * The exchange targets ONLY our trusted API origin (same-origin, or the exact configured
 * `VITE_API_ORIGIN`) via {@link resolveApiUrl}; a token is never posted to a third party.
 */

const START_ROUTE = '/api/auth/github/start';
const EXCHANGE_ROUTE = '/api/auth/github/exchange';

/** Safe, user-facing error codes surfaced by the callback or exchange. */
export type GitHubErrorCode =
  | 'access_denied'
  | 'login_failed'
  | 'not_authorized'
  | 'invalid_code'
  | 'exchange_failed';

interface GitHubExchangeResponse {
  readonly token: string;
  readonly tokenType: string;
  readonly expiresInSeconds: number;
  readonly subject: string;
}

type Listener = () => void;

export class GitHubSessionProvider implements SessionProvider {
  readonly mode = 'github' as const;
  readonly capabilities: ProviderCapabilities = FULL_CAPABILITIES;
  readonly requiresGate = true;

  private readonly store = new SessionCredentialStore('github');
  private readonly listeners = new Set<Listener>();
  private state: SessionState = { status: 'initializing' };
  private initialized = false;

  async initialize(): Promise<void> {
    if (this.initialized) return;
    this.initialized = true;

    const { code, error } = this.consumeCallbackParams();

    if (error) {
      this.setState({ status: 'error', errorCode: error });
      return;
    }

    if (code) {
      await this.exchange(code);
      return;
    }

    // No callback params: authenticated iff a same-tab session token survives.
    this.setState(
      this.store.getToken() ? { status: 'authenticated' } : { status: 'unauthenticated' },
    );
  }

  /** Begin login with a top-level navigation to the fixed start route. No return URL is accepted. */
  startLogin(): void {
    this.setState({ status: 'authenticating' });
    if (typeof window !== 'undefined') {
      window.location.assign(resolveApiUrl(START_ROUTE));
    }
  }

  /** Retry after an error returns to an interactive unauthenticated state. */
  retry(): void {
    this.setState({ status: 'unauthenticated' });
  }

  async acquireToken(): Promise<string | null> {
    // Short-lived, no refresh: the store returns null once expired, which drives a re-login.
    return this.store.getToken();
  }

  logout(): void {
    // Clear only OUR session token. There is no GitHub provider-logout assumption — we never held a
    // provider session in the browser, so we do not attempt to sign the user out of github.com.
    this.store.clear();
    this.setState({ status: 'unauthenticated' });
  }

  /** 401 from the API after a retry: the session token is gone/rejected — clear and re-gate. */
  handleAuthRequired(): void {
    this.store.clear();
    this.setState({ status: 'unauthenticated' });
  }

  /** 403 from the API: authenticated but the GitHub identity is not on the allowlist. */
  handleForbidden(): void {
    this.store.clear();
    this.setState({ status: 'error', errorCode: 'not_authorized' });
  }

  getState(): SessionState {
    return this.state;
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /**
   * Reads `code`/`error` from the current URL and strips the whole query string immediately via
   * `history.replaceState`, so the one-time code can never be reloaded, bookmarked, or replayed.
   */
  private consumeCallbackParams(): { code: string | null; error: GitHubErrorCode | null } {
    if (typeof window === 'undefined' || !window.location) {
      return { code: null, error: null };
    }
    const url = new URL(window.location.href);
    const code = url.searchParams.get('code');
    const rawError = url.searchParams.get('error');

    if (!code && !rawError) {
      return { code: null, error: null };
    }

    // Strip sensitive params from the address bar before anything else.
    url.searchParams.delete('code');
    url.searchParams.delete('error');
    const cleaned = `${url.pathname}${url.search}${url.hash}`;
    try {
      window.history.replaceState(window.history.state, '', cleaned);
    } catch {
      // Non-fatal: exchange still proceeds even if the URL could not be rewritten.
    }

    return { code, error: rawError ? this.normalizeError(rawError) : null };
  }

  private normalizeError(raw: string): GitHubErrorCode {
    switch (raw) {
      case 'access_denied':
      case 'login_failed':
      case 'not_authorized':
      case 'invalid_code':
        return raw;
      default:
        // Never surface an unrecognized provider string; collapse to a generic failure.
        return 'login_failed';
    }
  }

  private async exchange(code: string): Promise<void> {
    this.setState({ status: 'authenticating' });
    try {
      const response = await fetch(resolveApiUrl(EXCHANGE_ROUTE), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code }),
      });

      if (!response.ok) {
        // A replayed/unknown/expired code returns 400 invalid_code; anything else is generic.
        this.setState({
          status: 'error',
          errorCode: response.status === 400 ? 'invalid_code' : 'exchange_failed',
        });
        return;
      }

      const data = (await response.json()) as GitHubExchangeResponse;
      if (!data?.token) {
        this.setState({ status: 'error', errorCode: 'exchange_failed' });
        return;
      }

      const expiresAt =
        typeof data.expiresInSeconds === 'number' && data.expiresInSeconds > 0
          ? Date.now() + data.expiresInSeconds * 1000
          : undefined;
      this.store.set({ token: data.token, expiresAt });
      this.setState({ status: 'authenticated' });
    } catch {
      this.setState({ status: 'error', errorCode: 'exchange_failed' });
    }
  }

  private setState(next: SessionState): void {
    this.state = next;
    for (const listener of this.listeners) listener();
  }
}
