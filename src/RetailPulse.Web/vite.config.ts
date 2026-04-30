import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// API target is provided by Aspire via the API_URL env var when run under the
// AppHost. When running Vite standalone (e.g., `npm run dev` outside Aspire),
// fall back to the conventional local API port.
const apiTarget = process.env.API_URL || 'http://localhost:5100';

export default defineConfig({
  plugins: [react()],
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
