import type { AuthMode } from '../authMode';

/**
 * Provider-neutral capability descriptor. Because the mode is fixed at build time, this is a
 * static object per deployment — the UI reads it to CENTRALLY hide/disable surfaces a given
 * provider must not expose. It is a usability layer only: the backend remains the authoritative
 * gate (an anonymous token is still 403'd on any disallowed route regardless of what the UI shows).
 */
export interface ProviderCapabilities {
  /** SignalR telemetry/streaming hubs may be started. */
  readonly realtimeHub: boolean;
  /** The Real-Time Telemetry drawer (live spans, agent routing, traces). */
  readonly telemetryPanel: boolean;
  /** Observability tab (AI Gateway cost, tokens, APIM metrics). */
  readonly observability: boolean;
  /** Approvals surface (pending approvals + history). */
  readonly approvals: boolean;
  /** Memory panel + memory-management actions. */
  readonly memory: boolean;
  /** Token-streaming chat responses. */
  readonly streaming: boolean;
  /** Conversation/data export. */
  readonly export: boolean;
  /** Any write-capable action (approve, configure, etc.). */
  readonly writeActions: boolean;
  /** Non-chat alternate views/tabs (promo, competitive, stores, financials, portfolio, …). */
  readonly alternateViews: boolean;
}

/** A short-lived bearer credential minted by a provider for REST + SignalR. */
export interface SessionCredential {
  readonly token: string;
  /** Epoch milliseconds when the token expires, if known. */
  readonly expiresAt?: number;
}

/** Discriminated auth state a provider gate renders from. */
export type SessionStatus =
  | 'initializing'
  | 'unauthenticated'
  | 'authenticating'
  | 'authenticated'
  | 'error';

export interface SessionState {
  readonly status: SessionStatus;
  /** Present only when status === 'error'; a safe, user-facing code (never a provider secret). */
  readonly errorCode?: string;
}

/**
 * The single provider-neutral contract every mode implements. Token acquisition for the global
 * `authorizedFetch` and for the SignalR `accessTokenFactory` funnels through {@link acquireToken},
 * so provider logic is never duplicated in components.
 */
export interface SessionProvider {
  readonly mode: AuthMode;
  readonly capabilities: ProviderCapabilities;
  /**
   * False only for the local-dev Entra pass-through (no gate, no token). True whenever a provider
   * gate must be mounted and the global fetch wrapper installed.
   */
  readonly requiresGate: boolean;

  /** One-time bootstrap (MSAL redirect completion, GitHub code redemption, …). Idempotent. */
  initialize(): Promise<void>;

  /** Acquire a bearer token for a protected `/api` REST call or a hub handshake, or null. */
  acquireToken(options?: { readonly forceRefresh?: boolean }): Promise<string | null>;

  /** Clear the credential locally (and, where applicable, the provider session). */
  logout(): Promise<void> | void;
}

/** Full-capability profile shared by the fully-authenticated providers (Entra, GitHub). */
export const FULL_CAPABILITIES: ProviderCapabilities = {
  realtimeHub: true,
  telemetryPanel: true,
  observability: true,
  approvals: true,
  memory: true,
  streaming: true,
  export: true,
  writeActions: true,
  alternateViews: true,
};

/**
 * Anonymous demo capability profile: read-only chat only. Everything that implies real-time
 * telemetry, memory, write actions, observability, export, or alternate operator views is off —
 * matching the backend's deny-by-default anonymous surface (bootstrap + `POST /api/chat` only).
 */
export const ANONYMOUS_CAPABILITIES: ProviderCapabilities = {
  realtimeHub: false,
  telemetryPanel: false,
  observability: false,
  approvals: false,
  memory: false,
  streaming: false,
  export: false,
  writeActions: false,
  alternateViews: false,
};
