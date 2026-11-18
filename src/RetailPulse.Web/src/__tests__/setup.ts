/// <reference types="vitest/jsdom" />
import '@testing-library/jest-dom/vitest';

// Node 26's Web Storage globals shadow jsdom's origin-scoped browser storage.
for (const name of ['localStorage', 'sessionStorage'] as const) {
  Object.defineProperty(window, name, {
    configurable: true,
    get: () => jsdom.window[name],
  });
}
