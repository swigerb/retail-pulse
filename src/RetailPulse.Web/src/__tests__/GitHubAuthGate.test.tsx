import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AUTH_FORBIDDEN_EVENT, AUTH_REQUIRED_EVENT } from '../auth/authorizedFetch';
import { GitHubAuthGate } from '../auth/gates/GitHubAuthGate';
import { GitHubSessionProvider } from '../auth/providers/githubProvider';

// resolveApiUrl uses import.meta.env.VITE_API_ORIGIN; leaving it unset keeps requests same-origin.

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const child = <div data-testid="protected-child">app</div>;

const STORAGE_KEY = 'retailpulse:session:github';
const START_PATH = '/api/auth/github/start';
const EXCHANGE_PATH = '/api/auth/github/exchange';

function tokenResponse(body: Record<string, unknown>, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

let fetchMock: ReturnType<typeof vi.fn>;
let assignMock: Mock<(url: string) => void>;
let replaceStateSpy: ReturnType<typeof vi.spyOn>;
let currentUrl: URL;

/** Point the mock location at a new URL (used to seed the callback query string). */
function navigate(path: string) {
  currentUrl = new URL(path, 'http://localhost:3000');
}

beforeEach(() => {
  window.sessionStorage.clear();
  fetchMock = vi.fn();
  window.fetch = fetchMock as unknown as typeof window.fetch;
  assignMock = vi.fn<(url: string) => void>();
  navigate('/');

  // jsdom's location.assign is not implemented and navigation is inert, so we back window.location
  // with a mutable URL. The history.replaceState spy rewrites it, so the provider's immediate
  // code/error stripping is observable exactly as it would be in a browser.
  Object.defineProperty(window, 'location', {
    configurable: true,
    value: {
      get href() {
        return currentUrl.href;
      },
      get origin() {
        return currentUrl.origin;
      },
      get search() {
        return currentUrl.search;
      },
      get pathname() {
        return currentUrl.pathname;
      },
      get hash() {
        return currentUrl.hash;
      },
      assign: (url: string) => assignMock(url),
    },
  });
  replaceStateSpy = vi
    .spyOn(window.history, 'replaceState')
    .mockImplementation((_state, _title, url) => {
      currentUrl = new URL(String(url), currentUrl.origin);
    });
});

afterEach(() => {
  replaceStateSpy.mockRestore();
  vi.restoreAllMocks();
});

async function mountInitialized(provider: GitHubSessionProvider) {
  await act(async () => {
    await provider.initialize();
  });
  render(wrap(<GitHubAuthGate provider={provider}>{child}</GitHubAuthGate>));
}

describe('GitHubAuthGate — start', () => {
  it('shows a branded "Continue with GitHub" button when unauthenticated', async () => {
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);

    const button = screen.getByTestId('auth-github-button');
    expect(button).toHaveTextContent(/Continue with GitHub/i);
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });

  it('navigates to the fixed same-origin start route (no user-supplied return URL)', async () => {
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);

    await userEvent.click(screen.getByTestId('auth-github-button'));

    expect(assignMock).toHaveBeenCalledTimes(1);
    expect(assignMock).toHaveBeenCalledWith(START_PATH);
    // The provider never appends a return/redirect query parameter.
    expect(String(assignMock.mock.calls[0][0])).not.toMatch(/return|redirect|url=/i);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('GitHubAuthGate — callback exchange', () => {
  it('redeems a one-time code, strips it from the URL, and renders the app', async () => {
    navigate('/?code=abc123&extra=keep');
    fetchMock.mockResolvedValueOnce(
      tokenResponse({ token: 'rp-session', tokenType: 'Bearer', expiresInSeconds: 900, subject: 's' }),
    );
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);

    expect(await screen.findByTestId('protected-child')).toBeInTheDocument();

    // Exchange POSTed to the exact same-origin exchange route with the code.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(EXCHANGE_PATH);
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ code: 'abc123' });

    // Code was stripped immediately via history.replaceState; unrelated params preserved.
    expect(replaceStateSpy).toHaveBeenCalled();
    expect(window.location.search).not.toContain('code=');
    expect(window.location.search).toContain('extra=keep');

    // Token stored session-only.
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toContain('rp-session');
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('shows a safe access-denied message for a callback error and offers retry', async () => {
    navigate('/?error=access_denied');
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);

    expect(await screen.findByTestId('auth-error')).toHaveTextContent(/cancelled/i);
    expect(window.location.search).not.toContain('error=');
    expect(fetchMock).not.toHaveBeenCalled();

    // Retry returns to the interactive sign-in button.
    await userEvent.click(screen.getByTestId('auth-retry-button'));
    expect(await screen.findByTestId('auth-github-button')).toBeInTheDocument();
  });

  it('maps a replayed/expired code (HTTP 400) to an invalid_code message', async () => {
    navigate('/?code=used');
    fetchMock.mockResolvedValueOnce(tokenResponse({ error: 'invalid_code' }, 400));
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);

    expect(await screen.findByTestId('auth-error')).toHaveTextContent(/expired or was already used/i);
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('does not replay the code on a reload (it was already stripped from the URL)', async () => {
    navigate('/?code=abc123');
    fetchMock.mockResolvedValueOnce(
      tokenResponse({ token: 't', tokenType: 'Bearer', expiresInSeconds: 900, subject: 's' }),
    );
    const provider = new GitHubSessionProvider();
    await act(async () => {
      await provider.initialize();
    });
    // Simulate a reload: a brand-new provider with the (now stripped) URL.
    expect(window.location.search).not.toContain('code=');
    const reloaded = new GitHubSessionProvider();
    await act(async () => {
      await reloaded.initialize();
    });
    // Only the first initialize exchanged; the reload finds a surviving token, no second POST.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(reloaded.getState().status).toBe('authenticated');
  });
});

describe('GitHubAuthGate — logout, 401, 403', () => {
  it('clears the token and re-gates on logout (no github.com logout assumption)', async () => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 't', expiresAt: Date.now() + 60000 }));
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      provider.logout();
    });

    expect(await screen.findByTestId('auth-github-button')).toBeInTheDocument();
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
    // Logout must not attempt any provider-side navigation.
    expect(assignMock).not.toHaveBeenCalled();
  });

  it('clears the token and re-gates on a persistent 401 (AUTH_REQUIRED)', async () => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 't', expiresAt: Date.now() + 60000 }));
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });

    expect(await screen.findByTestId('auth-github-button')).toBeInTheDocument();
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('shows a not-authorized message on a 403 (AUTH_FORBIDDEN)', async () => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 't', expiresAt: Date.now() + 60000 }));
    const provider = new GitHubSessionProvider();
    await mountInitialized(provider);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });

    expect(await screen.findByTestId('auth-error')).toHaveTextContent(/not authorized/i);
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });
});

describe('GitHubSessionProvider — token acquisition', () => {
  it('returns the stored token, and null after it expires', async () => {
    const provider = new GitHubSessionProvider();
    window.sessionStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: 'live', expiresAt: Date.now() + 60000 }),
    );
    await expect(provider.acquireToken()).resolves.toBe('live');

    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 'stale', expiresAt: Date.now() - 1 }));
    // A fresh provider so the in-memory cache does not mask the expired sessionStorage value.
    const expired = new GitHubSessionProvider();
    await expect(expired.acquireToken()).resolves.toBeNull();
    // The expired credential self-clears from sessionStorage.
    await waitFor(() => expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull());
  });
});
