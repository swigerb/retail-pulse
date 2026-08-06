import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { authConfig } from './authConfig';
import { getMsalInstance } from './msalInstance';

/**
 * Central token acquisition for the Retail Pulse SPA. Every protected REST call and both
 * SignalR hub connections obtain their bearer token here so there is exactly one place that
 * talks to MSAL, requests the delegated API scope, and handles silent-refresh failures.
 */

export interface AcquireTokenOptions {
  /** When silent acquisition needs interaction, redirect to sign in (navigates away). */
  readonly interactive?: boolean;
  /** Force a fresh token from Entra (used on a 401 retry). */
  readonly forceRefresh?: boolean;
}

/**
 * Returns an access token for the API, or null when the user is not signed in (the sign-in
 * gate, not an individual fetch, is responsible for starting interactive login). Throws only
 * on unexpected MSAL errors so callers can distinguish "not signed in" from "broken".
 */
export async function acquireApiToken(options: AcquireTokenOptions = {}): Promise<string | null> {
  if (!authConfig.isConfigured) {
    return null;
  }

  const msal = getMsalInstance();
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null;
  if (!account) {
    return null;
  }

  try {
    const result = await msal.acquireTokenSilent({
      scopes: authConfig.apiScopes,
      account,
      forceRefresh: options.forceRefresh ?? false,
    });
    return result.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      if (options.interactive) {
        await msal.acquireTokenRedirect({ scopes: authConfig.apiScopes, account });
        return null; // redirect navigates away; nothing more to do here
      }
      return null;
    }
    throw error;
  }
}

/**
 * SignalR `accessTokenFactory`. Always resolves to a string ('' when unauthenticated or
 * unconfigured) because SignalR treats a non-empty return as the bearer token and an empty
 * string as "send no token" — the local dev hub works without a token.
 */
export async function getHubAccessToken(): Promise<string> {
  try {
    return (await acquireApiToken()) ?? '';
  } catch {
    return '';
  }
}
