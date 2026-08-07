import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AUTH_REQUIRED_EVENT } from '../auth/authorizedFetch';
import {
  AnonymousAuthGate,
  AnonymousSessionBanner,
} from '../auth/gates/AnonymousAuthGate';
import { AnonymousSessionProvider } from '../auth/providers/anonymousProvider';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const child = <div data-testid="protected-child">chat</div>;
const STORAGE_KEY = 'retailpulse:session:anonymous';
const BOOTSTRAP_PATH = '/api/auth/anonymous/session';

function tokenResponse(body: Record<string, unknown>, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  window.sessionStorage.clear();
  fetchMock = vi.fn();
  window.fetch = fetchMock as unknown as typeof window.fetch;
});

afterEach(() => {
  vi.restoreAllMocks();
});

async function mountInitialized(provider: AnonymousSessionProvider) {
  await act(async () => {
    await provider.initialize();
  });
  render(wrap(<AnonymousAuthGate provider={provider}>{child}</AnonymousAuthGate>));
}

describe('AnonymousAuthGate — consent screen', () => {
  it('shows the limited-demo warning, limitations, and a clear consent button', async () => {
    const provider = new AnonymousSessionProvider();
    await mountInitialized(provider);

    expect(screen.getByTestId('anon-warning-badge')).toHaveTextContent(/billable/i);
    expect(screen.getByTestId('anon-continue-button')).toHaveTextContent(
      /Continue in limited demo/i,
    );

    const limitations = screen.getByTestId('anon-limitations');
    expect(limitations).toHaveTextContent(/rate-limited/i);
    expect(limitations).toHaveTextContent(/read-only chat/i);
    expect(limitations).toHaveTextContent(/no.*telemetry|streaming|memory/i);
    expect(limitations).toHaveTextContent(/short-lived|expiry|restart/i);

    // Nothing billable happens before an explicit click.
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });

  it('bootstraps a session-only token ONLY on explicit consent click, then renders chat', async () => {
    fetchMock.mockResolvedValueOnce(
      tokenResponse({ token: 'anon-tok', tokenType: 'Bearer', expiresInSeconds: 600, subject: 'x' }),
    );
    const provider = new AnonymousSessionProvider();
    await mountInitialized(provider);

    await userEvent.click(screen.getByTestId('anon-continue-button'));

    expect(await screen.findByTestId('protected-child')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(BOOTSTRAP_PATH);
    expect(init.method).toBe('POST');

    // Session-only storage; never localStorage.
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toContain('anon-tok');
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('surfaces a rate-limited message on HTTP 429 and offers retry', async () => {
    fetchMock.mockResolvedValueOnce(tokenResponse({ error: 'rate_limited' }, 429));
    const provider = new AnonymousSessionProvider();
    await mountInitialized(provider);

    await userEvent.click(screen.getByTestId('anon-continue-button'));

    expect(await screen.findByTestId('auth-error')).toHaveTextContent(/rate limit/i);
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();

    // The button becomes a retry; a subsequent success renders the app.
    fetchMock.mockResolvedValueOnce(
      tokenResponse({ token: 't2', tokenType: 'Bearer', expiresInSeconds: 600, subject: 'x' }),
    );
    await userEvent.click(screen.getByTestId('anon-continue-button')); // retry → unauthenticated
    await userEvent.click(screen.getByTestId('anon-continue-button')); // consent again
    expect(await screen.findByTestId('protected-child')).toBeInTheDocument();
  });

  it('returns to the consent screen on a 401 (AUTH_REQUIRED) and clears the token', async () => {
    window.sessionStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: 't', expiresAt: Date.now() + 60000 }),
    );
    const provider = new AnonymousSessionProvider();
    await mountInitialized(provider);
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_REQUIRED_EVENT));
    });

    expect(await screen.findByTestId('anon-continue-button')).toBeInTheDocument();
    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });
});

describe('AnonymousSessionBanner — in-app expiry & session controls', () => {
  it('shows remaining time and a clear-session action', async () => {
    window.sessionStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: 't', expiresAt: Date.now() + 120000 }),
    );
    const provider = new AnonymousSessionProvider();
    await act(async () => {
      await provider.initialize();
    });

    render(wrap(<AnonymousSessionBanner provider={provider} />));

    expect(screen.getByTestId('anon-session-banner')).toBeInTheDocument();
    expect(screen.getByTestId('anon-expiry')).toHaveTextContent(/expires in/i);
    expect(screen.getByTestId('anon-new-session')).toBeInTheDocument();
    expect(screen.getByTestId('anon-clear-session')).toBeInTheDocument();
  });

  it('clears the session token when "Clear session" is clicked', async () => {
    window.sessionStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: 't', expiresAt: Date.now() + 120000 }),
    );
    const provider = new AnonymousSessionProvider();
    await act(async () => {
      await provider.initialize();
    });
    render(wrap(<AnonymousSessionBanner provider={provider} />));

    await userEvent.click(screen.getByTestId('anon-clear-session'));

    expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(provider.getState().status).toBe('unauthenticated');
  });

  it('mints a fresh token when "New anonymous session" is clicked', async () => {
    window.sessionStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ token: 'old', expiresAt: Date.now() + 120000 }),
    );
    const provider = new AnonymousSessionProvider();
    await act(async () => {
      await provider.initialize();
    });
    render(wrap(<AnonymousSessionBanner provider={provider} />));

    fetchMock.mockResolvedValueOnce(
      tokenResponse({ token: 'new', tokenType: 'Bearer', expiresInSeconds: 600, subject: 'x' }),
    );
    await userEvent.click(screen.getByTestId('anon-new-session'));

    await waitFor(() => expect(window.sessionStorage.getItem(STORAGE_KEY)).toContain('new'));
    expect(fetchMock).toHaveBeenCalledWith(BOOTSTRAP_PATH, expect.objectContaining({ method: 'POST' }));
  });
});

describe('AnonymousSessionProvider — capabilities', () => {
  it('exposes an all-false capability profile (read-only chat only)', () => {
    const provider = new AnonymousSessionProvider();
    const caps = provider.capabilities;
    expect(caps.realtimeHub).toBe(false);
    expect(caps.telemetryPanel).toBe(false);
    expect(caps.observability).toBe(false);
    expect(caps.approvals).toBe(false);
    expect(caps.memory).toBe(false);
    expect(caps.streaming).toBe(false);
    expect(caps.export).toBe(false);
    expect(caps.writeActions).toBe(false);
    expect(caps.alternateViews).toBe(false);
  });
});
