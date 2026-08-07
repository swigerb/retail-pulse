import { authMode } from './authMode';
import type { AuthMode } from './authMode';
import type { ProviderCapabilities, SessionProvider } from './session/types';
import { EntraSessionProvider } from './providers/entraProvider';
import { GitHubSessionProvider } from './providers/githubProvider';
import { AnonymousSessionProvider } from './providers/anonymousProvider';

/**
 * Selects and exposes the single build-time-configured session provider. Every consumer — the
 * global `authorizedFetch`, the SignalR `accessTokenFactory`, and the provider-neutral `AuthGate` —
 * reads the active provider from here, so provider logic is never duplicated across components.
 */

function createProvider(): SessionProvider {
  switch (authMode.mode) {
    case 'entra':
      return new EntraSessionProvider(authMode.isLocalDevPassthrough);
    case 'github':
      return new GitHubSessionProvider();
    case 'anonymous':
      return new AnonymousSessionProvider();
    default: {
      // Exhaustiveness guard — resolveAuthMode already fails closed on unknown values.
      const never: never = authMode.mode;
      throw new Error(`Unhandled authentication mode: ${String(never)}`);
    }
  }
}

let provider: SessionProvider | null = null;

export function getActiveProvider(): SessionProvider {
  if (!provider) {
    provider = createProvider();
  }
  return provider;
}

/** The active mode for this build. */
export const activeAuthMode: AuthMode = authMode.mode;

/** The active provider's capability descriptor — the central switch the UI reads to gate surfaces. */
export const capabilities: ProviderCapabilities = getActiveProvider().capabilities;

/** True when a provider gate must be mounted (i.e. not the local-dev Entra pass-through). */
export const requiresGate: boolean = getActiveProvider().requiresGate;

/**
 * Central credential acquisition used by REST global fetch and SignalR. Delegates to the active
 * provider so there is exactly one place that knows how each mode mints/refreshes a bearer token.
 */
export function acquireActiveToken(options?: { readonly forceRefresh?: boolean }): Promise<string | null> {
  return getActiveProvider().acquireToken(options);
}
