// @ts-check
/**
 * Shared, deterministic auth-mode build-metadata helpers for the Retail Pulse SPA
 * (Sprint 4, epic #27).
 *
 * The deployed SPA carries an IMMUTABLE, build-time record of the single authentication mode
 * it was compiled for, as a `<meta name="retail-pulse-auth-mode" content="...">` tag baked into
 * `index.html` by the Vite build (see `vite.config.ts`). All three providers are statically
 * bundled, so a JS "string absence" scan can never prove which mode is active — the meta tag is
 * the authoritative, verifiable signal instead.
 *
 * The value is NORMALIZED to a fixed enum (`Entra` / `GitHub` / `Anonymous`) at build time, so:
 *   • an operator cannot inject arbitrary markup via `VITE_AUTH_MODE` (no HTML injection), and
 *   • the value cannot be overridden at runtime (it is static text in the served document).
 *
 * The read-only production verifier (`scripts/Verify-ProductionAuth.ps1`) fetches the SWA root
 * and applies the SAME parser + predicate (mirrored in PowerShell) to assert the content is
 * exactly `Entra` (case-insensitive). A missing or malformed tag is a FAILURE.
 */

/** The `name` attribute of the immutable auth-mode meta tag. */
export const AUTH_MODE_META_NAME = 'retail-pulse-auth-mode';

/** Canonical, normalized provider names. The meta content is always one of these (or empty). */
const CANONICAL = /** @type {const} */ ({
  entra: 'Entra',
  github: 'GitHub',
  anonymous: 'Anonymous',
});

/**
 * Normalize a raw `VITE_AUTH_MODE` value to its canonical name, or `null` when it is
 * unset/blank or not a recognized mode. Unknown selectors never resolve to a provider.
 * @param {string | undefined | null} raw
 * @returns {string | null}
 */
export function normalizeAuthMode(raw) {
  const v = (raw ?? '').trim().toLowerCase();
  if (v === '') return null;
  return Object.prototype.hasOwnProperty.call(CANONICAL, v)
    ? CANONICAL[/** @type {keyof typeof CANONICAL} */ (v)]
    : null;
}

/**
 * Extract the auth-mode meta content from an HTML document. Tolerant of attribute order and of
 * single/double quotes. Returns the raw content string (possibly empty) when the tag is present,
 * or `null` when there is no such tag. Mirrors the PowerShell parser in Verify-ProductionAuth.ps1.
 * @param {string | undefined | null} html
 * @returns {string | null}
 */
export function parseAuthModeMeta(html) {
  if (typeof html !== 'string' || html.length === 0) return null;
  const metaTags = html.match(/<meta\b[^>]*>/gi);
  if (!metaTags) return null;
  for (const tag of metaTags) {
    const name = /\bname\s*=\s*["']([^"']*)["']/i.exec(tag);
    if (name && name[1].trim().toLowerCase() === AUTH_MODE_META_NAME) {
      const content = /\bcontent\s*=\s*["']([^"']*)["']/i.exec(tag);
      return content ? content[1] : '';
    }
  }
  return null;
}

/**
 * The production predicate: the served SPA is the Entra (production) build IFF the auth-mode meta
 * content is exactly `Entra`, case-insensitively. Anything else — including `GitHub`, `Anonymous`,
 * empty, or `null` (missing/malformed) — is NOT the production-Entra posture.
 * @param {string | null | undefined} content
 * @returns {boolean}
 */
export function isProductionEntra(content) {
  return typeof content === 'string' && content.trim().toLowerCase() === 'entra';
}

/**
 * Render the immutable auth-mode meta tag for a given raw mode. The content is normalized; an
 * unset/unknown mode yields an empty content (a build that pins no provider is not production-Entra).
 * @param {string | undefined | null} rawMode
 * @returns {string}
 */
export function renderAuthModeMetaTag(rawMode) {
  const content = normalizeAuthMode(rawMode) ?? '';
  return `<meta name="${AUTH_MODE_META_NAME}" content="${content}" />`;
}
