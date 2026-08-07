import { describe, it, expect, beforeEach } from 'vitest';
import { SessionCredentialStore } from '../auth/session/sessionCredentialStore';

const KEY = 'retailpulse:session:test-ns';

beforeEach(() => {
  window.sessionStorage.clear();
  window.localStorage.clear();
});

describe('SessionCredentialStore — narrow storage policy', () => {
  it('mirrors the credential to sessionStorage only, never localStorage or cookies', () => {
    const store = new SessionCredentialStore('test-ns');
    store.set({ token: 'abc', expiresAt: Date.now() + 60000 });

    expect(window.sessionStorage.getItem(KEY)).toContain('abc');
    expect(window.localStorage.getItem(KEY)).toBeNull();
    expect(document.cookie).not.toContain('abc');
  });

  it('returns the token while live and clears it on expiry', () => {
    const store = new SessionCredentialStore('test-ns');
    const now = 1_000_000;
    store.set({ token: 'live', expiresAt: now + 1000 });

    expect(store.getToken(now)).toBe('live');
    // At/after expiry the store self-clears and returns null.
    expect(store.getToken(now + 1000)).toBeNull();
    expect(window.sessionStorage.getItem(KEY)).toBeNull();
  });

  it('clear() removes the credential from memory and sessionStorage', () => {
    const store = new SessionCredentialStore('test-ns');
    store.set({ token: 'abc' });
    store.clear();

    expect(store.getToken()).toBeNull();
    expect(window.sessionStorage.getItem(KEY)).toBeNull();
  });

  it('rehydrates a same-tab credential from sessionStorage into a fresh instance', () => {
    window.sessionStorage.setItem(KEY, JSON.stringify({ token: 'persisted', expiresAt: Date.now() + 60000 }));
    const store = new SessionCredentialStore('test-ns');
    expect(store.getToken()).toBe('persisted');
  });

  it('discards a malformed/empty persisted value', () => {
    window.sessionStorage.setItem(KEY, '{not-json');
    const store = new SessionCredentialStore('test-ns');
    expect(store.getToken()).toBeNull();
    expect(window.sessionStorage.getItem(KEY)).toBeNull();
  });

  it('notifies subscribers on set and clear', () => {
    const store = new SessionCredentialStore('test-ns');
    let hits = 0;
    const unsub = store.subscribe(() => {
      hits += 1;
    });
    store.set({ token: 'a' });
    store.clear();
    unsub();
    store.set({ token: 'b' }); // no longer observed
    expect(hits).toBe(2);
  });
});
