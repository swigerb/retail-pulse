import { describe, it, expect } from 'vitest';
import {
  buildAuthConfig,
  validateEntraConfig,
  assertEntraConfigured,
  type RawAuthEnv,
} from '../auth/authConfig';

const ORIGIN = 'https://retail-pulse.example.net';

describe('buildAuthConfig', () => {
  it('is unconfigured (and requests no scopes) when tenant/client are missing', () => {
    const cfg = buildAuthConfig({}, ORIGIN);
    expect(cfg.isConfigured).toBe(false);
    expect(cfg.apiScopes).toEqual([]);
    expect(cfg.loginRequest.scopes).toEqual([]);
  });

  it('is unconfigured when only one of tenant/client is present', () => {
    expect(buildAuthConfig({ VITE_ENTRA_TENANT_ID: 't' }, ORIGIN).isConfigured).toBe(false);
    expect(buildAuthConfig({ VITE_ENTRA_CLIENT_ID: 'c' }, ORIGIN).isConfigured).toBe(false);
  });

  it('resolves a single-tenant authority and default api:// scope when configured', () => {
    const env: RawAuthEnv = {
      VITE_ENTRA_TENANT_ID: '11111111-1111-1111-1111-111111111111',
      VITE_ENTRA_CLIENT_ID: '33333333-3333-3333-3333-333333333333',
    };
    const cfg = buildAuthConfig(env, ORIGIN);

    expect(cfg.isConfigured).toBe(true);
    expect(cfg.msalConfig.auth.authority).toBe(
      'https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111',
    );
    expect(cfg.msalConfig.auth.clientId).toBe('33333333-3333-3333-3333-333333333333');
    // Default audience is the App ID URI form, default scope is access_as_user.
    expect(cfg.apiScopes).toEqual([
      'api://33333333-3333-3333-3333-333333333333/access_as_user',
    ]);
  });

  it('honours an explicit audience and custom scope name', () => {
    const cfg = buildAuthConfig(
      {
        VITE_ENTRA_TENANT_ID: 'tenant',
        VITE_ENTRA_CLIENT_ID: 'client',
        VITE_ENTRA_AUDIENCE: 'api://retail-pulse',
        VITE_ENTRA_API_SCOPE: 'access_as_user',
      },
      ORIGIN,
    );
    expect(cfg.apiScopes).toEqual(['api://retail-pulse/access_as_user']);
  });

  it('sets redirect URIs to the provided origin and never persists tokens in localStorage', () => {
    const cfg = buildAuthConfig(
      { VITE_ENTRA_TENANT_ID: 't', VITE_ENTRA_CLIENT_ID: 'c' },
      ORIGIN,
    );
    expect(cfg.msalConfig.auth.redirectUri).toBe(ORIGIN);
    expect(cfg.msalConfig.auth.postLogoutRedirectUri).toBe(ORIGIN);
    expect(cfg.msalConfig.cache?.cacheLocation).toBe('sessionStorage');
  });

  it('supports a sovereign/custom Entra instance and normalizes trailing slashes', () => {
    const cfg = buildAuthConfig(
      {
        VITE_ENTRA_TENANT_ID: 'tenant',
        VITE_ENTRA_CLIENT_ID: 'client',
        VITE_ENTRA_INSTANCE: 'https://login.microsoftonline.us/',
      },
      ORIGIN,
    );
    expect(cfg.msalConfig.auth.authority).toBe('https://login.microsoftonline.us/tenant');
  });
});

const VALID_TENANT = '11111111-1111-1111-1111-111111111111';
const VALID_CLIENT = '33333333-3333-3333-3333-333333333333';

describe('validateEntraConfig', () => {
  it('accepts a valid GUID tenant + GUID client', () => {
    expect(validateEntraConfig(VALID_TENANT, VALID_CLIENT)).toEqual({ ok: true });
  });

  it('accepts a verified directory domain as the tenant', () => {
    expect(validateEntraConfig('contoso.onmicrosoft.com', VALID_CLIENT).ok).toBe(true);
  });

  it.each([
    ['', VALID_CLIENT, 'empty tenant'],
    [VALID_TENANT, '', 'empty client'],
    ['   ', VALID_CLIENT, 'whitespace tenant'],
    ['<your-tenant-id>', VALID_CLIENT, 'angle-bracket placeholder tenant'],
    [VALID_TENANT, '<your-client-id>', 'angle-bracket placeholder client'],
    ['00000000-0000-0000-0000-000000000000', VALID_CLIENT, 'all-zero GUID tenant'],
    [VALID_TENANT, '00000000-0000-0000-0000-000000000000', 'all-zero GUID client'],
    ['your-tenant-id', VALID_CLIENT, 'scaffold token tenant'],
    ['not-a-guid-or-domain', VALID_CLIENT, 'malformed tenant'],
    [VALID_TENANT, 'not-a-guid', 'non-GUID client'],
    ['tenant', VALID_CLIENT, 'non-GUID non-domain tenant'],
  ])('rejects %s / %s (%s)', (tenant, client) => {
    const result = validateEntraConfig(tenant, client);
    expect(result.ok).toBe(false);
    expect(result.error).toBeTruthy();
  });
});

describe('assertEntraConfigured', () => {
  it('does not throw for a valid configuration', () => {
    const cfg = buildAuthConfig(
      { VITE_ENTRA_TENANT_ID: VALID_TENANT, VITE_ENTRA_CLIENT_ID: VALID_CLIENT },
      ORIGIN,
    );
    expect(() => assertEntraConfigured(cfg)).not.toThrow();
  });

  it('throws a deterministic configuration error when ids are missing', () => {
    const cfg = buildAuthConfig({}, ORIGIN);
    expect(() => assertEntraConfigured(cfg)).toThrowError(/configuration is invalid/i);
  });

  it('throws when ids are placeholders even though buildAuthConfig marks it "configured"', () => {
    const cfg = buildAuthConfig(
      { VITE_ENTRA_TENANT_ID: '<tenant>', VITE_ENTRA_CLIENT_ID: '<client>' },
      ORIGIN,
    );
    // isConfigured is true (both non-empty) but the values are placeholders — must still fail closed.
    expect(cfg.isConfigured).toBe(true);
    expect(() => assertEntraConfigured(cfg)).toThrow();
  });
});
