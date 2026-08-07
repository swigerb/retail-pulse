import { describe, it, expect } from 'vitest';
import {
  AUTH_MODE_META_NAME,
  normalizeAuthMode,
  parseAuthModeMeta,
  isProductionEntra,
  renderAuthModeMetaTag,
} from '../../scripts/auth-mode-meta.mjs';

/**
 * Behavioral coverage for the SHARED auth-mode build-metadata parser + predicate
 * (scripts/auth-mode-meta.mjs), which is the single source of truth used by BOTH:
 *   • the Vite build (bakes the `<meta name="retail-pulse-auth-mode">` tag), and
 *   • the read-only production verifier (Verify-ProductionAuth.ps1 mirrors this parser/predicate).
 *
 * The critical, behavioral guarantee (mirrors the real emitted-build assertions in
 * scripts/provider-build-matrix.mjs): the Entra build's marker satisfies the production-Entra
 * predicate, while the GitHub, Anonymous, empty, and missing markers all FAIL it. This is what
 * makes an accidental GitHub/Anonymous production deployment detectable instead of relying on an
 * impossible "string absence" scan of a statically-bundled SPA.
 */

describe('auth-mode-meta — normalizeAuthMode', () => {
  it('canonicalizes recognized modes case-insensitively', () => {
    expect(normalizeAuthMode('Entra')).toBe('Entra');
    expect(normalizeAuthMode('entra')).toBe('Entra');
    expect(normalizeAuthMode('  ENTRA  ')).toBe('Entra');
    expect(normalizeAuthMode('GitHub')).toBe('GitHub');
    expect(normalizeAuthMode('github')).toBe('GitHub');
    expect(normalizeAuthMode('Anonymous')).toBe('Anonymous');
  });

  it('returns null for unset/blank or unknown selectors (never picks a provider)', () => {
    expect(normalizeAuthMode(undefined)).toBeNull();
    expect(normalizeAuthMode(null)).toBeNull();
    expect(normalizeAuthMode('')).toBeNull();
    expect(normalizeAuthMode('   ')).toBeNull();
    expect(normalizeAuthMode('Okta')).toBeNull();
    expect(normalizeAuthMode('Entra ')).toBe('Entra'); // trimmed
    expect(normalizeAuthMode('Ent')).toBeNull();
  });
});

describe('auth-mode-meta — parseAuthModeMeta', () => {
  it('extracts content regardless of attribute order and quote style', () => {
    expect(parseAuthModeMeta(`<meta name="${AUTH_MODE_META_NAME}" content="Entra">`)).toBe('Entra');
    expect(parseAuthModeMeta(`<meta content='GitHub' name='${AUTH_MODE_META_NAME}'/>`)).toBe('GitHub');
    expect(
      parseAuthModeMeta(`<head><meta charset="utf-8"><meta name="${AUTH_MODE_META_NAME}" content="Anonymous"></head>`),
    ).toBe('Anonymous');
  });

  it('returns empty string when the tag is present but content is empty', () => {
    expect(parseAuthModeMeta(`<meta name="${AUTH_MODE_META_NAME}" content="">`)).toBe('');
  });

  it('returns null when there is no such tag (missing/malformed)', () => {
    expect(parseAuthModeMeta('')).toBeNull();
    expect(parseAuthModeMeta(null)).toBeNull();
    expect(parseAuthModeMeta(undefined)).toBeNull();
    expect(parseAuthModeMeta('<html><head></head></html>')).toBeNull();
    expect(parseAuthModeMeta('<meta name="description" content="Entra">')).toBeNull();
  });
});

describe('auth-mode-meta — isProductionEntra predicate', () => {
  it('passes only for exactly Entra (case-insensitive)', () => {
    expect(isProductionEntra('Entra')).toBe(true);
    expect(isProductionEntra('entra')).toBe(true);
    expect(isProductionEntra('  Entra ')).toBe(true);
  });

  it('fails for GitHub, Anonymous, empty, and missing markers', () => {
    expect(isProductionEntra('GitHub')).toBe(false);
    expect(isProductionEntra('Anonymous')).toBe(false);
    expect(isProductionEntra('')).toBe(false);
    expect(isProductionEntra(null)).toBe(false);
    expect(isProductionEntra(undefined)).toBe(false);
  });
});

describe('auth-mode-meta — behavioral build → parse → predicate round-trip', () => {
  // Mirror the real emitted-build assertions: the tag a given VITE_AUTH_MODE would bake into
  // index.html must parse back to its normalized name and satisfy the predicate ONLY for Entra.
  it('Entra build marker satisfies the production-Entra predicate', () => {
    const html = renderAuthModeMetaTag('Entra');
    const content = parseAuthModeMeta(html);
    expect(content).toBe('Entra');
    expect(isProductionEntra(content)).toBe(true);
  });

  it.each(['GitHub', 'Anonymous'])('%s build marker FAILS the production-Entra predicate', (mode) => {
    const html = renderAuthModeMetaTag(mode);
    const content = parseAuthModeMeta(html);
    expect(content).toBe(mode);
    expect(isProductionEntra(content)).toBe(false);
  });

  it('an unset/unknown build bakes an empty marker that fails the predicate', () => {
    const html = renderAuthModeMetaTag(undefined);
    const content = parseAuthModeMeta(html);
    expect(content).toBe('');
    expect(isProductionEntra(content)).toBe(false);
  });
});
