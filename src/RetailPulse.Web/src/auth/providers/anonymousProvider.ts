import { resolveApiUrl } from '../../config/apiOrigin';
import { SessionCredentialStore } from '../session/sessionCredentialStore';
import {
  ANONYMOUS_CAPABILITIES,
  type ProviderCapabilities,
  type SessionProvider,
  type SessionState,
} from '../session/types';

/**
 * Anonymous limited-demo session provider (opt-in, non-production).
 *
 * Nothing happens until the user gives EXPLICIT consent: {@link bootstrap} POSTs
 * `POST /api/auth/anonymous/session`, which mints a short-lived, no-refresh session token (random
 * subject, no PII). The token is stored session-only and used as the bearer for the single
 * allowlisted capability, `POST /api/chat`. There is no provider redirect and no cross-tab sharing.
 *
 * The request targets ONLY our trusted API origin (same-origin, or the exact configured
 * `VITE_API_ORIGIN`) via {@link resolveApiUrl}. When the token expires the store returns null and
 * the gate offers a fresh "New anonymous session".
 */

const BOOTSTRAP_ROUTE = '/api/auth/anonymous/session';

export type AnonymousErrorCode = 'rate_limited' | 'bootstrap_failed';

interface AnonymousBootstrapResponse {
  readonly token: string;
  readonly tokenType: string;
  readonly expiresInSeconds: number;
  readonly subject: string;
}

type Listener = () => void;

export class AnonymousSessionProvider implements SessionProvider {
  readonly mode = 'anonymous' as const;
  readonly capabilities: ProviderCapabilities = ANONYMOUS_CAPABILITIES;
  readonly requiresGate = true;

  private readonly store = new SessionCredentialStore('anonymous');
  private readonly listeners = new Set<Listener>();
  private state: SessionState = { status: 'initializing' };
  private initialized = false;

  async initialize(): Promise<void> {
    if (this.initialized) return;
    this.initialized = true;
    // Never auto-start a billable session — require an explicit consent click. A surviving same-tab
    // token (reload) is honoured so the demo does not force re-consent on refresh.
    this.setState(
      this.store.getToken() ? { status: 'authenticated' } : { status: 'unauthenticated' },
    );
    // Expire the local view when the token TTL lapses so the UI flips back to the consent screen.
    this.scheduleExpiryWatch();
  }

  /** Explicit user consent → mint a short-lived anonymous session token. */
  async bootstrap(): Promise<void> {
    this.setState({ status: 'authenticating' });
    try {
      const response = await fetch(resolveApiUrl(BOOTSTRAP_ROUTE), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
      });

      if (!response.ok) {
        this.setState({
          status: 'error',
          errorCode: response.status === 429 ? 'rate_limited' : 'bootstrap_failed',
        });
        return;
      }

      const data = (await response.json()) as AnonymousBootstrapResponse;
      if (!data?.token) {
        this.setState({ status: 'error', errorCode: 'bootstrap_failed' });
        return;
      }

      const expiresAt =
        typeof data.expiresInSeconds === 'number' && data.expiresInSeconds > 0
          ? Date.now() + data.expiresInSeconds * 1000
          : undefined;
      this.store.set({ token: data.token, expiresAt });
      this.setState({ status: 'authenticated' });
      this.scheduleExpiryWatch();
    } catch {
      this.setState({ status: 'error', errorCode: 'bootstrap_failed' });
    }
  }

  /** Clear the current session and return to the consent screen (no new session is minted). */
  endSession(): void {
    this.store.clear();
    this.setState({ status: 'unauthenticated' });
  }

  /** "New anonymous session": clear then immediately bootstrap a fresh one. */
  async newSession(): Promise<void> {
    this.store.clear();
    await this.bootstrap();
  }

  retry(): void {
    this.setState({ status: 'unauthenticated' });
  }

  async acquireToken(): Promise<string | null> {
    return this.store.getToken();
  }

  logout(): void {
    this.endSession();
  }

  /** 401 from the API: the short-lived session token expired/was rejected — return to consent. */
  handleAuthRequired(): void {
    this.endSession();
  }

  /** Milliseconds until the current token expires, or null when unknown/none. */
  msUntilExpiry(now: number = Date.now()): number | null {
    const credential = this.store.get(now);
    if (!credential || credential.expiresAt === undefined) return null;
    return Math.max(0, credential.expiresAt - now);
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

  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  private scheduleExpiryWatch(): void {
    if (this.expiryTimer) {
      clearTimeout(this.expiryTimer);
      this.expiryTimer = null;
    }
    const remaining = this.msUntilExpiry();
    if (remaining === null || typeof setTimeout === 'undefined') return;
    this.expiryTimer = setTimeout(() => {
      // get() self-clears an expired credential; flip the gate back to consent.
      if (!this.store.getToken()) {
        this.setState({ status: 'unauthenticated' });
      }
    }, remaining + 50);
  }

  private setState(next: SessionState): void {
    this.state = next;
    for (const listener of this.listeners) listener();
  }
}
