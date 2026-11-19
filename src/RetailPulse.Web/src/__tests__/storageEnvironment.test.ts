import { describe, expect, it } from 'vitest';

describe('browser storage test environment', () => {
  it.each(['localStorage', 'sessionStorage'] as const)('uses jsdom %s, not Node Web Storage', (name) => {
    expect(window[name]).toBe(jsdom.window[name]);
    expect(globalThis[name]).toBe(jsdom.window[name]);
    expect(window[name]).toBeInstanceOf(window.Storage);
  });

  it('keeps functional local and session storage separate', () => {
    const key = 'storage-environment-test';
    try {
      window.sessionStorage.setItem(key, 'session');
      window.localStorage.setItem(key, 'local');
      expect(window.sessionStorage.getItem(key)).toBe('session');
      expect(window.localStorage.getItem(key)).toBe('local');
    } finally {
      window.sessionStorage.removeItem(key);
      window.localStorage.removeItem(key);
    }
  });
});
