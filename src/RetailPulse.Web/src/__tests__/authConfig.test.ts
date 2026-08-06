import { describe, it, expect } from 'vitest';
import { buildAuthConfig, type RawAuthEnv } from '../auth/authConfig';

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
