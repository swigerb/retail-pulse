import {
  EventType,
  PublicClientApplication,
  type AuthenticationResult,
} from '@azure/msal-browser';
import { authConfig } from './authConfig';

/**
 * Lazily-created MSAL PublicClientApplication singleton.
 *
 * Construction is deferred so importing this module never throws when auth is unconfigured
 * (local dev) — MSAL requires a non-empty clientId. Only the configured production build
 * calls {@link getMsalInstance}/{@link initializeMsal}; the dev build renders straight to
 * the app and relies on the API's Development synthetic-auth handler.
 */
let instance: PublicClientApplication | null = null;
let initialized: Promise<void> | null = null;

export function getMsalInstance(): PublicClientApplication {
  if (!authConfig.isConfigured) {
    throw new Error(
      'MSAL requested but Entra auth is not configured (VITE_ENTRA_TENANT_ID/VITE_ENTRA_CLIENT_ID missing).',
    );
  }
  if (!instance) {
    instance = new PublicClientApplication(authConfig.msalConfig);
  }
  return instance;
}

/**
 * Initializes MSAL exactly once: completes any redirect sign-in, sets the active account,
 * and keeps it fresh on subsequent login/token events. Idempotent under React StrictMode.
 */
export function initializeMsal(): Promise<void> {
  if (initialized) {
    return initialized;
  }

  initialized = (async () => {
    const msal = getMsalInstance();
    await msal.initialize();

    const redirectResult = await msal.handleRedirectPromise();
    if (redirectResult?.account) {
      msal.setActiveAccount(redirectResult.account);
    } else if (!msal.getActiveAccount()) {
      const [firstAccount] = msal.getAllAccounts();
      if (firstAccount) {
        msal.setActiveAccount(firstAccount);
      }
    }

    msal.addEventCallback((event) => {
      if (
        (event.eventType === EventType.LOGIN_SUCCESS ||
          event.eventType === EventType.ACQUIRE_TOKEN_SUCCESS) &&
        event.payload
      ) {
        const payload = event.payload as AuthenticationResult;
        if (payload.account) {
          msal.setActiveAccount(payload.account);
        }
      }
    });
  })();

  return initialized;
}
