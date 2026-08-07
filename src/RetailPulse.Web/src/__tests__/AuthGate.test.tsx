import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { InteractionStatus } from '@azure/msal-browser';
import { AUTH_FORBIDDEN_EVENT } from '../auth/authorizedFetch';

// The dispatcher reads the active provider selection. A mutable mock lets each test pick the mode
// and whether a gate is required, exercising the build-time dispatch without a real provider.
const { mockActive } = vi.hoisted(() => ({
  mockActive: {
    requiresGate: true,
    activeAuthMode: 'entra' as 'entra' | 'github' | 'anonymous',
    provider: {} as unknown,
  },
}));
vi.mock('../auth/activeProvider', () => ({
  get requiresGate() {
    return mockActive.requiresGate;
  },
  get activeAuthMode() {
    return mockActive.activeAuthMode;
  },
  getActiveProvider: () => mockActive.provider,
}));

// authConfig/msal are only consumed by the Entra gate.
vi.mock('../auth/authConfig', () => ({
  authConfig: { isConfigured: true },
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

/** Minimal observable-provider stub for GitHub/Anonymous dispatch checks. */
function stubProvider(status: string) {
  const state = { status };
  return {
    subscribe: () => () => {},
    getState: () => state,
    startLogin: vi.fn(),
    bootstrap: vi.fn(),
    retry: vi.fn(),
    handleAuthRequired: vi.fn(),
    handleForbidden: vi.fn(),
    msUntilExpiry: () => null,
    endSession: vi.fn(),
    newSession: vi.fn(),
  };
}

beforeEach(() => {
  mockActive.requiresGate = true;
  mockActive.activeAuthMode = 'entra';
  mockActive.provider = {};
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

describe('AuthGate dispatcher', () => {
  it('is a transparent pass-through when no gate is required (local dev)', () => {
    mockActive.requiresGate = false;
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
  });

  it('dispatches to the GitHub gate in github mode', () => {
    mockActive.activeAuthMode = 'github';
    mockActive.provider = stubProvider('unauthenticated');
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('auth-github-button')).toHaveTextContent(/Continue with GitHub/i);
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });

  it('dispatches to the Anonymous gate in anonymous mode', () => {
    mockActive.activeAuthMode = 'anonymous';
    mockActive.provider = stubProvider('unauthenticated');
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('anon-continue-button')).toHaveTextContent(
      /Continue in limited demo/i,
    );
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });
});

describe('AuthGate → Entra gate (live UX, unchanged)', () => {
  it('renders the protected app when the user is authenticated', () => {
    useIsAuthenticated.mockReturnValue(true);
    render(wrap(<AuthGate>{child}</AuthGate>));
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();
    expect(screen.queryByTestId('auth-gate')).not.toBeInTheDocument();
  });

  it('shows the Microsoft sign-in gate (and hides the app) when unauthenticated', async () => {
    useIsAuthenticated.mockReturnValue(false);
    render(wrap(<AuthGate>{child}</AuthGate>));

    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
    const button = screen.getByTestId('auth-signin-button');
    expect(button).toHaveTextContent(/Sign in with Microsoft/i);

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
    expect(screen.getByTestId('protected-child')).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new CustomEvent(AUTH_FORBIDDEN_EVENT));
    });
    expect(await screen.findByTestId('auth-forbidden')).toBeInTheDocument();
    expect(screen.queryByTestId('protected-child')).not.toBeInTheDocument();
  });
});
