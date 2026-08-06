/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_ORIGIN?: string;
  // ── Microsoft Entra SPA auth (single-tenant, PKCE, no secret) ──────────────
  // Build-time configuration (NOT secrets). Injected by infra/main.bicep → azd env
  // → Vite build. Blank locally so the dev build uses the API's synthetic dev auth.
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  readonly VITE_ENTRA_API_SCOPE?: string;
  readonly VITE_ENTRA_AUDIENCE?: string;
  readonly VITE_ENTRA_INSTANCE?: string;
  readonly VITE_FEATURE_CAMPAIGN_PLANNER?: string;
  readonly VITE_FEATURE_COMPETITIVE?: string;
  readonly VITE_FEATURE_KNOWLEDGE_BASE?: string;
  readonly VITE_FEATURE_HEALTH_COUNCIL?: string;
  readonly VITE_FEATURE_SECURITY?: string;
  readonly VITE_FEATURE_CARDS?: string;
  readonly VITE_FEATURE_STORES?: string;
  readonly VITE_FEATURE_FINANCIALS?: string;
  readonly VITE_FEATURE_PORTFOLIO?: string;
  readonly VITE_FEATURE_OBSERVABILITY?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
