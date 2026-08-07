import type { ReactNode } from 'react';
import { activeAuthMode, getActiveProvider, requiresGate } from './activeProvider';
import { EntraAuthGate } from './gates/EntraAuthGate';
import { GitHubAuthGate } from './gates/GitHubAuthGate';
import { AnonymousAuthGate } from './gates/AnonymousAuthGate';
import type { GitHubSessionProvider } from './providers/githubProvider';
import type { AnonymousSessionProvider } from './providers/anonymousProvider';

/**
 * Provider-neutral sign-in gate dispatcher.
 *
 * The SPA renders exactly ONE provider's sign-in UX, fixed at build time by `VITE_AUTH_MODE`
 * (see authMode). There is deliberately NO provider chooser in a single-mode deployment — only the
 * configured mode is ever rendered, minimizing attack surface and user confusion. When no gate is
 * required (the local-dev Entra pass-through) the gate is a transparent pass-through so the demo
 * runs against the API's synthetic Development identity.
 *
 * Each concrete gate preserves its provider's exact behavior:
 *   • Entra     → the unchanged live MSAL Microsoft sign-in UX.
 *   • GitHub    → "Continue with GitHub" BFF redemption flow.
 *   • Anonymous → "Continue in limited demo" explicit-consent flow.
 */
export function AuthGate({ children }: { children: ReactNode }) {
  if (!requiresGate) {
    return <>{children}</>;
  }

  switch (activeAuthMode) {
    case 'github':
      return (
        <GitHubAuthGate provider={getActiveProvider() as unknown as GitHubSessionProvider}>
          {children}
        </GitHubAuthGate>
      );
    case 'anonymous':
      return (
        <AnonymousAuthGate provider={getActiveProvider() as unknown as AnonymousSessionProvider}>
          {children}
        </AnonymousAuthGate>
      );
    case 'entra':
    default:
      return <EntraAuthGate>{children}</EntraAuthGate>;
  }
}
