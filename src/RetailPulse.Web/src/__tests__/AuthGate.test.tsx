import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { InteractionStatus } from '@azure/msal-browser';
import { AUTH_FORBIDDEN_EVENT } from '../auth/authorizedFetch';

// Mutable so individual tests can flip configured/unconfigured; AuthGate reads it per render.
const { mockAuthConfig } = vi.hoisted(() => ({ mockAuthConfig: { isConfigured: true } }));
vi.mock('../auth/authConfig', () => ({
  authConfig: mockAuthConfig,
  loginRequest: { scopes: ['api://client/access_as_user'] },
}));

const useIsAuthenticated = vi.fn();
const loginRedirect = vi.fn();
const logoutRedirect = vi.fn();
const useMsal = vi.fn();
vi.mock('@azure/msal-react', () => ({
  useIsAuthenticated: () => useIsAuthenticated(),
  useMsal: () => useMsal(),
}));

// Imported after the mocks above are registered.
import { AuthGate } from '../auth/AuthGate';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const child = <div data-testid="protected-child">dashboard</div>;

beforeEach(() => {
  mockAuthConfig.isConfigured = true;
  useIsAuthenticated.mockReset();
  useMsal.mockReset();
  loginRedirect.mockReset();
  logoutRedirect.mockReset();
  useMsal.mockReturnValue({
    instance: { loginRedirect, logoutRedirect },
    inProgress: InteractionStatus.None,
    accounts: [],
  });
});

describe('AuthGate', () => {
  it('is a transparent pass-through when auth is not configured (local dev)', () => {
    mockAuthConfig.isConfigured = false;
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
  });

  it('renders the protected app when the user is authenticated', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
  });

  it('shows the sign-in gate (and hides the app) when unauthenticated', async () => {
    useIsAuthenticated.mockReturnValue(false);
    render(wrap(<AuthGate>{child}</AuthGate>));

    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
    const button = screen.getByTestId('auth-signin-button');
    expect(button).toBeInTheDocument();

    await userEvent.click(button);
    expect(loginRedirect).toHaveBeenCalledTimes(1);
  });

  it('shows a spinner while sign-in is in progress', () => {
    useIsAuthenticated.mockReturnValue(false);
    useMsal.mockReturnValue({
      instance: { loginRedirect, logoutRedirect },
      inProgress: InteractionStatus.Login,
      accounts: [],
    });
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('auth-signing-in')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-signin-button')).not.toBeInTheDocument();
  });

  it('surfaces a role/scope access-denied message on a 403 event', async () => {
    useIsAuthenticated.mockReturnValue(true);
    render(wrap(<AuthGate>{child}</AuthGate>));
    // Authenticated user with the app rendered...
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    // ...then the API returns 403 (authenticated but unassigned): the gate takes over.
    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });
    expect(await screen.findByTestId('auth-forbidden')).toBeInTheDocument();
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });
});
