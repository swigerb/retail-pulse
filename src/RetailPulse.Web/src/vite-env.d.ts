/// <reference types="vite/client" />

interface ImportMetaEnv {
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
