import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { authConfig } from '../authConfig';
import { getMsalInstance } from '../msalInstance';
import { FULL_CAPABILITIES, type ProviderCapabilities, type SessionProvider } from '../session/types';

/**
 * Entra session provider — the live production path. This preserves the EXACT prior MSAL behavior:
 * silent acquisition of the delegated API scope for the active account, a redirect to interactive
 * sign-in only when explicitly requested (never silently from a data fetch), and a null return when
 * the user is not signed in so the sign-in gate (not an individual fetch) owns interactive login.
 *
 * The Entra tokens continue to live in MSAL's `sessionStorage` cache (see authConfig) — this
 * provider does not use the Retail Pulse session store, which is only for the GitHub/Anonymous
 * short-lived tokens.
 */
export class EntraSessionProvider implements SessionProvider {
  readonly mode = 'entra' as const;
  readonly capabilities: ProviderCapabilities = FULL_CAPABILITIES;

  /** Local dev with no Entra config is a transparent pass-through: no gate, no token. */
  private readonly isLocalDevPassthrough: boolean;

  constructor(isLocalDevPassthrough: boolean) {
    this.isLocalDevPassthrough = isLocalDevPassthrough;
  }

  get requiresGate(): boolean {
    // Outside the explicit local-dev pass-through, an Entra build ALWAYS requires the sign-in gate.
    // We deliberately do NOT let authConfig.isConfigured turn the gate off: a missing/placeholder
    // configuration must fail closed (assertEntraConfigured throws in main.tsx before render), never
    // silently drop the gate and expose an unauthenticated shell.
    return !this.isLocalDevPassthrough;
  }

  async initialize(): Promise<void> {
    // MSAL initialization (redirect completion + active-account wiring) is handled in main.tsx via
    // initializeMsal() before render, keeping the MsalProvider/React lifecycle intact. Nothing to do
    // here for the pass-through case.
  }

  async acquireToken(options: { readonly forceRefresh?: boolean } = {}): Promise<string | null> {
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
        // The sign-in gate is responsible for starting interactive login; a fetch never triggers it.
        return null;
      }
      throw error;
    }
  }

  logout(): void {
    if (!authConfig.isConfigured) return;
    void getMsalInstance().logoutRedirect();
  }
}
