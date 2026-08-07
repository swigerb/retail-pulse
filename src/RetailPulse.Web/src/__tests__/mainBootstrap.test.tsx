import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Bootstrap-level tests for the fail-closed Entra path (Blocker 2). They prove that an explicit Entra
 * build with missing/placeholder configuration renders the safe branded configuration-error screen and
 * makes NO MSAL/API/hub calls and never mounts <App/>, while a valid Entra build boots unchanged.
 *
 * main.tsx runs `void bootstrap()` on import, so each test wires the module mocks, imports main fresh
 * (vi.resetModules), and asserts on the effects.
 */

const initializeMsal = vi.fn(async () => {});
const installAuthorizedFetch = vi.fn();
const getActiveProviderInit = vi.fn(async () => {});

vi.mock('../App.tsx', () => ({
  default: () => <div data-testid="app-root">app</div>,
}));

vi.mock('@azure/msal-react', () => ({
  MsalProvider: ({ children }: { children: React.ReactNode }) => (
    <div data-testid="msal-provider">{children}</div>
  ),
}));

vi.mock('../auth/msalInstance', () => ({
  initializeMsal,
  getMsalInstance: () => ({}) as unknown,
}));

vi.mock('../auth/authorizedFetch', () => ({
  installAuthorizedFetch,
}));

let rootEl: HTMLElement;

beforeEach(() => {
  vi.resetModules();
  initializeMsal.mockClear();
  installAuthorizedFetch.mockClear();
  getActiveProviderInit.mockClear();
  document.body.innerHTML = '';
  rootEl = document.createElement('div');
  rootEl.id = 'root';
  document.body.appendChild(rootEl);
});

afterEach(() => {
  vi.restoreAllMocks();
});

/** Flush the microtasks the async bootstrap schedules so the DOM settles before assertions. */
async function flush() {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('bootstrap — explicit Entra with missing/placeholder config fails closed', () => {
  it('renders the branded config-error screen and makes NO MSAL/API/hub/App calls', async () => {
    vi.doMock('../auth/activeProvider', () => ({
      activeAuthMode: 'entra',
      requiresGate: true,
      getActiveProvider: () => ({ initialize: getActiveProviderInit }),
    }));
    vi.doMock('../auth/authConfig', () => ({
      assertEntraConfigured: () => {
        throw new Error('Entra authentication is selected but its configuration is invalid.');
      },
    }));

    await import('../main.tsx');
    await flush();

    // Safe branded error screen shown; dashboard/App never mounted.
    expect(document.querySelector('[data-testid="config-error-screen"]')).not.toBeNull();
    expect(document.querySelector('[data-testid="app-root"]')).toBeNull();
    expect(document.querySelector('[data-testid="msal-provider"]')).toBeNull();

    // No MSAL init, no fetch wrapper install — i.e. no API/hub calls were set up.
    expect(initializeMsal).not.toHaveBeenCalled();
    expect(installAuthorizedFetch).not.toHaveBeenCalled();
  });
});

describe('bootstrap — valid Entra boots unchanged', () => {
  it('initializes MSAL, installs the fetch wrapper, and mounts App inside MsalProvider', async () => {
    vi.doMock('../auth/activeProvider', () => ({
      activeAuthMode: 'entra',
      requiresGate: true,
      getActiveProvider: () => ({ initialize: getActiveProviderInit }),
    }));
    vi.doMock('../auth/authConfig', () => ({
      assertEntraConfigured: () => {},
    }));

    await import('../main.tsx');
    await flush();

    expect(document.querySelector('[data-testid="config-error-screen"]')).toBeNull();
    expect(document.querySelector('[data-testid="app-root"]')).not.toBeNull();
    expect(document.querySelector('[data-testid="msal-provider"]')).not.toBeNull();
    expect(initializeMsal).toHaveBeenCalledTimes(1);
    expect(installAuthorizedFetch).toHaveBeenCalledTimes(1);
  });
});
