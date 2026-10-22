import '@testing-library/jest-dom/vitest';

// Node 26 exposes an unavailable process-level localStorage unless it receives
// --localstorage-file. That accessor can shadow jsdom's Storage implementation.
if (!window.localStorage) {
  const entries = new Map<string, string>();
  const storage: Storage = {
    get length() {
      return entries.size;
    },
    clear: () => entries.clear(),
    getItem: key => entries.get(key) ?? null,
    key: index => [...entries.keys()][index] ?? null,
    removeItem: key => entries.delete(key),
    setItem: (key, value) => entries.set(key, String(value)),
  };
  Object.defineProperty(window, 'localStorage', { configurable: true, value: storage });
}
