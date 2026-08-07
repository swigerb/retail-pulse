import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import type { Plugin } from 'vite';
import { AUTH_MODE_META_NAME, normalizeAuthMode } from './scripts/auth-mode-meta.mjs';

// API target is provided by Aspire via the API_URL env var when run under the
// AppHost. When running Vite standalone (e.g., `npm run dev` outside Aspire),
// fall back to the conventional local API port.
const apiTarget = process.env.API_URL || 'http://localhost:5100';

/**
 * Bakes an IMMUTABLE, build-time record of the active authentication mode into the emitted
 * index.html as `<meta name="retail-pulse-auth-mode" content="Entra|GitHub|Anonymous">`.
 *
 * The value is NORMALIZED to a fixed enum at build time (never the raw VITE_AUTH_MODE), so it
 * cannot carry injected markup and cannot be overridden at runtime — it is static text in the
 * served document. All providers are statically bundled; this tag (not a JS string scan) is the
 * authoritative signal the read-only production verifier asserts against.
 */
function authModeMetaPlugin(): Plugin {
  return {
    name: 'retail-pulse-auth-mode-meta',
    transformIndexHtml: {
      order: 'pre',
      handler() {
        const content = normalizeAuthMode(process.env.VITE_AUTH_MODE) ?? '';
        return [
          {
            tag: 'meta',
            attrs: { name: AUTH_MODE_META_NAME, content },
            injectTo: 'head-prepend',
          },
        ];
      },
    },
  };
}

export default defineConfig({
  plugins: [react(), authModeMetaPlugin()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
      },
      '/hubs': {
        target: apiTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
