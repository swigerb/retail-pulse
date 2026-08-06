import type { Configuration, RedirectRequest } from '@azure/msal-browser';
import { LogLevel } from '@azure/msal-browser';

/**
 * Single-tenant Microsoft Entra SPA configuration for Retail Pulse.
 *
 * The SPA uses the OAuth authorization-code + PKCE flow with NO client secret. Tenant ID,
 * client ID, the delegated API scope and the API audience are build-time CONFIGURATION
 * (not secrets) injected as `VITE_ENTRA_*` variables by the deployment (`infra/main.bicep`
 * → azd env → Vite build). Left blank locally so the dev build runs against the API's
 * Development synthetic-auth handler without contacting Entra.
 */
export interface RawAuthEnv {
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  readonly VITE_ENTRA_API_SCOPE?: string;
  readonly VITE_ENTRA_AUDIENCE?: string;
  readonly VITE_ENTRA_INSTANCE?: string;
}

export interface ResolvedAuthConfig {
  /** True only when a tenant and client id are both present — gates all MSAL usage. */
  readonly isConfigured: boolean;
  readonly tenantId: string;
  readonly clientId: string;
  /** Fully-qualified delegated scope(s) the SPA requests, e.g. api://{clientId}/access_as_user. */
  readonly apiScopes: string[];
  readonly msalConfig: Configuration;
  readonly loginRequest: RedirectRequest;
}

const DEFAULT_INSTANCE = 'https://login.microsoftonline.com/';
const DEFAULT_API_SCOPE = 'access_as_user';

function trimTrailingSlashes(value: string): string {
  return value.replace(/\/+$/, '');
}

/**
 * Pure resolver so the configuration is unit-testable with arbitrary env inputs.
 * `origin` is injected (defaults to the browser origin) for the redirect URIs.
 */
export function buildAuthConfig(
  env: RawAuthEnv,
  origin: string = typeof window !== 'undefined' ? window.location.origin : '',
): ResolvedAuthConfig {
  const tenantId = (env.VITE_ENTRA_TENANT_ID ?? '').trim();
  const clientId = (env.VITE_ENTRA_CLIENT_ID ?? '').trim();
  const apiScope = (env.VITE_ENTRA_API_SCOPE ?? '').trim() || DEFAULT_API_SCOPE;
  const instance = trimTrailingSlashes((env.VITE_ENTRA_INSTANCE ?? '').trim() || DEFAULT_INSTANCE);
  // Audience defaults to the App ID URI form api://{clientId}; an explicit override wins.
  const audience = (env.VITE_ENTRA_AUDIENCE ?? '').trim() || (clientId ? `api://${clientId}` : '');

  const isConfigured = Boolean(tenantId && clientId);
  const apiScopes = isConfigured ? [`${trimTrailingSlashes(audience)}/${apiScope}`] : [];

  const msalConfig: Configuration = {
    auth: {
      clientId,
      authority: `${instance}/${tenantId}`,
      redirectUri: origin,
      postLogoutRedirectUri: origin,
      navigateToLoginRequestUrl: true,
    },
    cache: {
      // sessionStorage keeps tokens out of long-lived localStorage; cleared on tab close.
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        // Never log PII; keep noise at Error level.
        piiLoggingEnabled: false,
        logLevel: LogLevel.Error,
        loggerCallback: () => {},
      },
    },
  };

  return {
    isConfigured,
    tenantId,
    clientId,
    apiScopes,
    msalConfig,
    loginRequest: { scopes: apiScopes },
  };
}

export const authConfig: ResolvedAuthConfig = buildAuthConfig(
  import.meta.env as unknown as RawAuthEnv,
);

/** Convenience re-exports for the common consumers. */
export const isAuthConfigured = authConfig.isConfigured;
export const apiScopes = authConfig.apiScopes;
export const loginRequest = authConfig.loginRequest;
export const msalConfig = authConfig.msalConfig;
