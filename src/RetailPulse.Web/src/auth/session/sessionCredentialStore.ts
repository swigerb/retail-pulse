import type { SessionCredential } from './types';

/**
 * A session-scoped credential store for the Retail Pulse short-lived session tokens minted by the
 * GitHub BFF and Anonymous bootstrap flows.
 *
 * Storage policy (deliberately narrow):
 *   - The live value is held in memory (module state) — the source of truth for token acquisition.
 *   - A copy is mirrored to `sessionStorage` under a namespaced key so a same-tab reload survives,
 *     but the credential dies on tab close and is NEVER shared across tabs/windows.
 *   - It is NEVER written to `localStorage` or a broadly-readable cookie.
 *
 * The GitHub PROVIDER token never reaches the browser at all; only our own session token does, and
 * only through this store. The token is cleared on logout, on expiry, and on a 401/403 from the API.
 */

const STORAGE_PREFIX = 'retailpulse:session:';

type Listener = () => void;

function safeSessionStorage(): Storage | null {
  try {
    if (typeof window === 'undefined' || !window.sessionStorage) return null;
    return window.sessionStorage;
  } catch {
    // Access can throw in sandboxed/embedded contexts; degrade to memory-only.
    return null;
  }
}

export class SessionCredentialStore {
  private readonly storageKey: string;
  private readonly listeners = new Set<Listener>();
  private credential: SessionCredential | null = null;
  private hydrated = false;

  constructor(namespace: string) {
    this.storageKey = `${STORAGE_PREFIX}${namespace}`;
  }

  /** Lazily rehydrate a same-tab credential from sessionStorage exactly once. */
  private hydrate(): void {
    if (this.hydrated) return;
    this.hydrated = true;
    const store = safeSessionStorage();
    if (!store) return;
    const raw = store.getItem(this.storageKey);
    if (!raw) return;
    try {
      const parsed = JSON.parse(raw) as SessionCredential;
      if (parsed && typeof parsed.token === 'string' && parsed.token.length > 0) {
        this.credential = parsed;
      } else {
        store.removeItem(this.storageKey);
      }
    } catch {
      store.removeItem(this.storageKey);
    }
  }

  /** Returns the current non-expired credential, clearing and returning null if it has expired. */
  get(now: number = Date.now()): SessionCredential | null {
    this.hydrate();
    const current = this.credential;
    if (!current) return null;
    if (current.expiresAt !== undefined && current.expiresAt <= now) {
      this.clear();
      return null;
    }
    return current;
  }

  /** The bearer token, or null when there is no live credential. */
  getToken(now: number = Date.now()): string | null {
    return this.get(now)?.token ?? null;
  }

  set(credential: SessionCredential): void {
    this.hydrate();
    this.credential = credential;
    const store = safeSessionStorage();
    if (store) {
      try {
        store.setItem(this.storageKey, JSON.stringify(credential));
      } catch {
        // Non-fatal: memory remains the source of truth.
      }
    }
    this.emit();
  }

  clear(): void {
    this.hydrate();
    const had = this.credential !== null;
    this.credential = null;
    const store = safeSessionStorage();
    if (store) {
      try {
        store.removeItem(this.storageKey);
      } catch {
        // ignore
      }
    }
    if (had) this.emit();
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private emit(): void {
    for (const listener of this.listeners) listener();
  }
}
