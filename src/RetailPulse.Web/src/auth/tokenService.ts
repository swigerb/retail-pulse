import { acquireActiveToken, getActiveProvider } from './activeProvider';

/**
 * Provider-neutral token acquisition for the Retail Pulse SPA. Every protected REST call and both
 * SignalR hub connections obtain their bearer token here so there is exactly ONE place that talks
 * to the active provider (Entra MSAL, GitHub BFF session, or Anonymous session). Provider-specific
 * logic lives in the providers behind {@link getActiveProvider}; this module never branches on mode.
 */

export interface AcquireTokenOptions {
  /**
   * Retained for source compatibility. Interactive sign-in is owned by the provider gate, not by an
   * individual fetch, so this flag no longer triggers a redirect from token acquisition.
   */
  readonly interactive?: boolean;
  /** Force a fresh token from the provider (used on a 401 retry). */
  readonly forceRefresh?: boolean;
}

/**
 * Returns a bearer token for the API, or null when the user is not signed in (the sign-in gate, not
 * an individual fetch, is responsible for starting interactive login). Throws only on an unexpected
 * provider error so callers can distinguish "not signed in" from "broken".
 */
export async function acquireApiToken(options: AcquireTokenOptions = {}): Promise<string | null> {
  return acquireActiveToken({ forceRefresh: options.forceRefresh ?? false });
}

/**
 * SignalR `accessTokenFactory`. Always resolves to a string ('' when unauthenticated, unconfigured,
 * or when the active provider's capabilities forbid the real-time hubs) because SignalR treats a
 * non-empty return as the bearer token and an empty string as "send no token". The Anonymous
 * provider disables the hubs, so this returns '' and the hub is never started for it (see Dashboard).
 */
export async function getHubAccessToken(): Promise<string> {
  if (!getActiveProvider().capabilities.realtimeHub) {
    return '';
  }
  try {
    return (await acquireApiToken()) ?? '';
  } catch {
    return '';
  }
}
