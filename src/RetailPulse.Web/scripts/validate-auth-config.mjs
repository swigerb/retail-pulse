// @ts-check
/**
 * Build-time / predeploy fail-closed guard for the Retail Pulse SPA (Blocker 2).
 *
 * A frontend-only deploy (e.g. Static Web Apps build) reads VITE_AUTH_MODE + VITE_ENTRA_* from the
 * environment. If the azd infra outputs are absent or the operator forgot to set the Entra ids, an
 * Entra build would otherwise embed empty configuration and silently ship an unauthenticated shell.
 * This validator runs as the npm `prebuild` step so `npm run build` FAILS FAST when an explicit Entra
 * build is missing/placeholder tenant/client ids.
 *
 * Contract (kept deliberately permissive so CI's plain `npm run build` stays green):
 *   • VITE_AUTH_MODE unset/blank        → PASS  (local dev / CI type-check build; no provider pinned).
 *   • VITE_AUTH_MODE = entra            → require VALID, non-placeholder tenant + client ids, else FAIL.
 *   • VITE_AUTH_MODE = github|anonymous → PASS  (Entra ids intentionally not required for these modes).
 *   • VITE_AUTH_MODE = anything else    → FAIL  (an unknown selector can never pick a provider).
 *
 * The tenant/client validation mirrors auth/authConfig.ts::validateEntraConfig so the deploy guard and
 * the runtime fail-closed guard agree.
 */

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
const TENANT_DOMAIN_RE = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$/i;
const KNOWN_MODES = ['entra', 'github', 'anonymous'];

/** @param {string} value */
function isPlaceholder(value) {
  const v = (value ?? '').trim();
  if (v === '' || v === EMPTY_GUID) return true;
  if (/[<>]/.test(v) || /\s/.test(v)) return true;
  return /(your[-_]?|placeholder|changeme|example|todo|xxxx+|\bfixme\b)/i.test(v);
}

/**
 * @param {string} tenantId
 * @param {string} clientId
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateEntraIds(tenantId, clientId) {
  const tenant = (tenantId ?? '').trim();
  const client = (clientId ?? '').trim();
  if (isPlaceholder(tenant)) return { ok: false, error: 'Entra tenant id is missing or a placeholder.' };
  if (!(GUID_RE.test(tenant) || TENANT_DOMAIN_RE.test(tenant))) {
    return { ok: false, error: 'Entra tenant id is not a valid GUID or directory domain.' };
  }
  if (isPlaceholder(client)) return { ok: false, error: 'Entra client id is missing or a placeholder.' };
  if (!GUID_RE.test(client)) return { ok: false, error: 'Entra client id is not a valid GUID.' };
  return { ok: true };
}

/**
 * Pure validator over an env-like record so it is unit-testable with arbitrary inputs.
 * @param {Record<string, string | undefined>} env
 * @returns {{ ok: boolean, error?: string }}
 */
export function validateAuthConfig(env) {
  const mode = (env.VITE_AUTH_MODE ?? '').trim().toLowerCase();

  // No pinned mode: nothing to validate (local dev / CI type-check build stays green).
  if (mode === '') return { ok: true };

  if (!KNOWN_MODES.includes(mode)) {
    return {
      ok: false,
      error: `VITE_AUTH_MODE="${env.VITE_AUTH_MODE}" is not a recognized authentication mode ` +
        `(one of: ${KNOWN_MODES.join(', ')}).`,
    };
  }

  // Only Entra requires the SPA-embedded tenant/client ids; github/anonymous never do.
  if (mode === 'entra') {
    const result = validateEntraIds(env.VITE_ENTRA_TENANT_ID ?? '', env.VITE_ENTRA_CLIENT_ID ?? '');
    if (!result.ok) {
      return {
        ok: false,
        error: `VITE_AUTH_MODE=Entra but ${result.error} ` +
          'Set non-empty, valid VITE_ENTRA_TENANT_ID and VITE_ENTRA_CLIENT_ID before building. ' +
          'Refusing to build an Entra SPA with empty/placeholder configuration.',
      };
    }
  }

  return { ok: true };
}

// CLI entry point: used as the npm `prebuild` guard. Exits non-zero (fails the build) on invalid config.
const invokedDirectly =
  typeof process !== 'undefined' &&
  Array.isArray(process.argv) &&
  process.argv[1] &&
  import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href;

if (invokedDirectly) {
  const result = validateAuthConfig(/** @type {Record<string,string|undefined>} */ (process.env));
  if (!result.ok) {
    console.error(`\n[validate-auth-config] BUILD BLOCKED: ${result.error}\n`);
    process.exit(1);
  }
  const mode = (process.env.VITE_AUTH_MODE ?? '').trim() || '(unset — no provider pinned)';
  console.log(`[validate-auth-config] OK — auth mode: ${mode}`);
}
