import { describe, it, expect } from 'vitest';
import { resolveAuthMode, type RawAuthModeEnv } from '../auth/authMode';

/**
 * Fail-closed resolver contract (never silently anonymous). The resolver is pure — env is injected —
 * so every production/unknown branch is unit-testable without touching the real build environment.
 */
describe('resolveAuthMode', () => {
  const entraConfig = {
    VITE_ENTRA_TENANT_ID: 'tenant-guid',
    VITE_ENTRA_CLIENT_ID: 'client-guid',
  };

  it('honors an explicit entra mode', () => {
    const r = resolveAuthMode({ VITE_AUTH_MODE: 'entra', ...entraConfig });
    expect(r.mode).toBe('entra');
    expect(r.source).toBe('explicit');
    expect(r.isLocalDevPassthrough).toBe(false);
  });

  it('honors an explicit github mode and always mounts a gate', () => {
    const r = resolveAuthMode({ VITE_AUTH_MODE: 'github', DEV: true });
    expect(r.mode).toBe('github');
    expect(r.source).toBe('explicit');
    expect(r.isLocalDevPassthrough).toBe(false);
  });

  it('honors an explicit anonymous mode and always mounts a gate', () => {
    const r = resolveAuthMode({ VITE_AUTH_MODE: 'anonymous', DEV: true });
    expect(r.mode).toBe('anonymous');
    expect(r.isLocalDevPassthrough).toBe(false);
  });

  it('is case-insensitive for explicit modes', () => {
    expect(resolveAuthMode({ VITE_AUTH_MODE: 'GitHub' }).mode).toBe('github');
    expect(resolveAuthMode({ VITE_AUTH_MODE: '  Anonymous  ' }).mode).toBe('anonymous');
    expect(resolveAuthMode({ VITE_AUTH_MODE: 'ENTRA', ...entraConfig }).mode).toBe('entra');
  });

  it('treats explicit entra without SPA config in local dev as a transparent pass-through', () => {
    const r = resolveAuthMode({ VITE_AUTH_MODE: 'entra', DEV: true });
    expect(r.mode).toBe('entra');
    expect(r.isLocalDevPassthrough).toBe(true);
  });

  it('does NOT pass through for explicit entra without config when NOT in dev', () => {
    const r = resolveAuthMode({ VITE_AUTH_MODE: 'entra', DEV: false });
    expect(r.mode).toBe('entra');
    expect(r.isLocalDevPassthrough).toBe(false);
  });

  it('resolves a missing mode to entra when the SPA already carries Entra config (back-compat)', () => {
    const r = resolveAuthMode({ ...entraConfig, DEV: false, PROD: true });
    expect(r.mode).toBe('entra');
    expect(r.source).toBe('entra-config-backcompat');
    expect(r.isLocalDevPassthrough).toBe(false);
  });

  it('resolves a missing mode to a dev pass-through when unconfigured and in dev', () => {
    const r = resolveAuthMode({ DEV: true });
    expect(r.mode).toBe('entra');
    expect(r.source).toBe('local-dev-default');
    expect(r.isLocalDevPassthrough).toBe(true);
  });

  it('THROWS on a missing mode in a production build with no Entra config (fail closed)', () => {
    expect(() => resolveAuthMode({ DEV: false, PROD: true })).toThrow(/not set/i);
  });

  it('THROWS on a blank/whitespace mode with no config in production', () => {
    expect(() => resolveAuthMode({ VITE_AUTH_MODE: '   ', DEV: false })).toThrow(/not set/i);
  });

  it('THROWS on an unknown named mode (e.g. okta)', () => {
    expect(() => resolveAuthMode({ VITE_AUTH_MODE: 'okta', DEV: true } as RawAuthModeEnv)).toThrow(
      /not a recognized/i,
    );
  });

  it('THROWS on a bare numeric selector', () => {
    expect(() => resolveAuthMode({ VITE_AUTH_MODE: '1', DEV: true })).toThrow(/not a recognized/i);
  });

  it('never resolves to anonymous by omission', () => {
    // With no explicit mode, the only silent resolutions are entra (safe) or a throw — never anonymous.
    expect(resolveAuthMode({ ...entraConfig }).mode).toBe('entra');
    expect(() => resolveAuthMode({ DEV: false })).toThrow();
  });
});
