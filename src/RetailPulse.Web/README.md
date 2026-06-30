# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

## Feature flags / Navigation

Retail Pulse keeps the default web navigation focused on **Chat**, **Real-Time Telemetry**, and **Observability**. Real-Time Telemetry is always visible and streams live agent spans, token totals, and cost estimates. Observability is enabled by default because it shows the AI Gateway via Azure APIM story: costs, token usage, and operational metrics.

Secondary demo tabs are configuration-gated and hidden by default. To enable optional tabs locally, copy `.env.example` to `.env.local` and set the matching `VITE_FEATURE_*` flag to `true` or `1`.

Available flags:

| Flag | Default | Tab |
|------|---------|-----|
| `VITE_FEATURE_CAMPAIGN_PLANNER` | `false` | Campaign Planner |
| `VITE_FEATURE_COMPETITIVE` | `false` | Competitive |
| `VITE_FEATURE_KNOWLEDGE_BASE` | `false` | Knowledge Base |
| `VITE_FEATURE_HEALTH_COUNCIL` | `false` | Health Council |
| `VITE_FEATURE_SECURITY` | `false` | Security |
| `VITE_FEATURE_CARDS` | `false` | Cards |
| `VITE_FEATURE_STORES` | `false` | Stores |
| `VITE_FEATURE_FINANCIALS` | `false` | Financials |
| `VITE_FEATURE_PORTFOLIO` | `false` | Portfolio |
| `VITE_FEATURE_OBSERVABILITY` | `true` | Observability |

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend updating the configuration to enable type-aware lint rules:

```js
export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...

      // Remove tseslint.configs.recommended and replace with this
      tseslint.configs.recommendedTypeChecked,
      // Alternatively, use this for stricter rules
      tseslint.configs.strictTypeChecked,
      // Optionally, add this for stylistic rules
      tseslint.configs.stylisticTypeChecked,

      // Other configs...
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```

You can also install [eslint-plugin-react-x](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-x) and [eslint-plugin-react-dom](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-dom) for React-specific lint rules:

```js
// eslint.config.js
import reactX from 'eslint-plugin-react-x'
import reactDom from 'eslint-plugin-react-dom'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...
      // Enable lint rules for React
      reactX.configs['recommended-typescript'],
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
