import { describe, it, expect, vi, beforeEach } from 'vitest';

/**
 * The SignalR access-token factory must return '' (send no token, do not start the hub) whenever the
 * active provider's capabilities forbid the real-time hubs — the Anonymous provider case. Entra and
 * GitHub (full capabilities) return the bearer. Provider selection is mocked so each mode is exercised.
 */
const acquireActiveToken = vi.fn();
const capabilities = { realtimeHub: true } as { realtimeHub: boolean };

vi.mock('../auth/activeProvider', () => ({
  acquireActiveToken: (...args: unknown[]) => acquireActiveToken(...args),
  getActiveProvider: () => ({ capabilities }),
}));

import { getHubAccessToken, acquireApiToken } from '../auth/tokenService';

beforeEach(() => {
  acquireActiveToken.mockReset();
  capabilities.realtimeHub = true;
});

describe('getHubAccessToken — capability gating', () => {
  it('returns the bearer token when the provider permits the real-time hub', async () => {
    acquireActiveToken.mockResolvedValue('hub-tok');
    await expect(getHubAccessToken()).resolves.toBe('hub-tok');
  });

  it('returns an empty string (no hub) when realtimeHub is disabled (anonymous)', async () => {
    capabilities.realtimeHub = false;
    await expect(getHubAccessToken()).resolves.toBe('');
    // Token acquisition is never even attempted for a forbidden hub.
    expect(acquireActiveToken).not.toHaveBeenCalled();
  });

  it('returns an empty string when acquisition throws', async () => {
    acquireActiveToken.mockRejectedValue(new Error('boom'));
    await expect(getHubAccessToken()).resolves.toBe('');
  });

  it('returns an empty string when there is no token', async () => {
    acquireActiveToken.mockResolvedValue(null);
    await expect(getHubAccessToken()).resolves.toBe('');
  });
});

describe('acquireApiToken — forceRefresh passthrough', () => {
  it('delegates forceRefresh to the active provider', async () => {
    acquireActiveToken.mockResolvedValue('t');
    await acquireApiToken({ forceRefresh: true });
    expect(acquireActiveToken).toHaveBeenCalledWith({ forceRefresh: true });
  });

  it('defaults forceRefresh to false', async () => {
    acquireActiveToken.mockResolvedValue('t');
    await acquireApiToken();
    expect(acquireActiveToken).toHaveBeenCalledWith({ forceRefresh: false });
  });
});
