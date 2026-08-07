/**
 * Build-time deterministic authentication-mode resolver for the Retail Pulse SPA.
 *
 * The SPA renders exactly ONE provider's sign-in UX, selected at build time by the
 * `VITE_AUTH_MODE` variable (injected by the deployment: `infra/main.bicep` → azd env →
 * Vite build). This mirrors the backend's `Authentication:Mode` resolver so the two halves
 * of a deployment agree on a single provider — a deployment contract test proves the parity.
 *
 * Fail-closed rules (never silently anonymous):
 *   - A recognized, explicit mode (`entra` / `github` / `anonymous`, any case) always wins.
 *   - A missing/blank mode resolves to `entra` ONLY when it is safe to do so:
 *       • a hosted build that already carries the Entra SPA configuration (back-compat with
 *         deployments that pin the mode on the API but not yet in the frontend build), or
 *       • an explicit local dev build (`import.meta.env.DEV`), where it becomes a transparent
 *         pass-through against the API's Development synthetic-auth handler.
 *   - A missing/blank mode in any OTHER case (a production build with no Entra config) THROWS,
 *     so the misconfiguration is visible at load instead of silently degrading.
 *   - An unknown value (e.g. `okta`) or a bare number (e.g. `1`) always THROWS — a numeric or
 *     unrecognized selector can never pick a provider.
 */

export type AuthMode = 'entra' | 'github' | 'anonymous';

export const AUTH_MODES: readonly AuthMode[] = ['entra', 'github', 'anonymous'];

export interface RawAuthModeEnv {
  readonly VITE_AUTH_MODE?: string;
  readonly VITE_ENTRA_TENANT_ID?: string;
  readonly VITE_ENTRA_CLIENT_ID?: string;
  /** Vite sets DEV=true for the dev server, PROD=true for a production `vite build`. */
  readonly DEV?: boolean;
  readonly PROD?: boolean;
}

export type AuthModeSource =
  | 'explicit'
  | 'entra-config-backcompat'
  | 'local-dev-default';

export interface ResolvedAuthMode {
  readonly mode: AuthMode;
  /**
   * True only for the local-dev, Entra-unconfigured case: no provider gate is mounted and the
   * app renders straight to the API's Development synthetic identity (today's dev experience).
   */
  readonly isLocalDevPassthrough: boolean;
  readonly source: AuthModeSource;
}

function isAuthMode(value: string): value is AuthMode {
  return (AUTH_MODES as readonly string[]).includes(value);
}

/**
 * Pure resolver so the fail-closed behavior is unit-testable with arbitrary env inputs.
 * Throws an Error (fail visibly) for a missing mode outside local dev and for any unknown value.
 */
export function resolveAuthMode(env: RawAuthModeEnv): ResolvedAuthMode {
  const raw = (env.VITE_AUTH_MODE ?? '').trim();
  const dev = env.DEV === true;
  const entraConfigured = Boolean(
    (env.VITE_ENTRA_TENANT_ID ?? '').trim() && (env.VITE_ENTRA_CLIENT_ID ?? '').trim(),
  );

  if (raw !== '') {
    const normalized = raw.toLowerCase();
    if (isAuthMode(normalized)) {
      // Entra with no SPA config in a local dev build is the transparent pass-through; every
      // other named mode always mounts its provider gate.
      const isLocalDevPassthrough = normalized === 'entra' && !entraConfigured && dev;
      return { mode: normalized, isLocalDevPassthrough, source: 'explicit' };
    }
    throw new Error(
      `VITE_AUTH_MODE="${raw}" is not a recognized authentication mode. ` +
        `Set it to one of: ${AUTH_MODES.join(', ')}.`,
    );
  }

  // Missing / blank mode.
  if (entraConfigured) {
    // Hosted Entra build that pins the mode on the API but not (yet) in the frontend build.
    // Resolving to Entra is safe — it is the live, secure provider, never a downgrade.
    return { mode: 'entra', isLocalDevPassthrough: false, source: 'entra-config-backcompat' };
  }

  if (dev) {
    // Explicit local dev: transparent pass-through against the API's Development auth handler.
    return { mode: 'entra', isLocalDevPassthrough: true, source: 'local-dev-default' };
  }

  throw new Error(
    'VITE_AUTH_MODE is not set and no Entra SPA configuration is present. ' +
      'A production build must pin an explicit authentication mode ' +
      `(one of: ${AUTH_MODES.join(', ')}) — refusing to start to avoid a silent, ` +
      'insecure default.',
  );
}

/** The resolved mode for this build. Throws at module load on a fail-closed misconfiguration. */
export const authMode: ResolvedAuthMode = resolveAuthMode(
  import.meta.env as unknown as RawAuthModeEnv,
);
