import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { GuardrailsConfig } from '../components/guardrails/GuardrailsConfig';
import type { GuardrailsConfigData } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

function baseConfig(overrides?: Partial<GuardrailsConfigData>): GuardrailsConfigData {
  return {
    piiDetectionEnabled: true,
    jailbreakDetectionEnabled: true,
    autoRedactPii: true,
    maxInputLength: 10000,
    piiPatterns: ['SSN', 'Email', 'Phone', 'CreditCard'],
    jailbreakPatterns: ['IgnoreInstructions', 'RolePlay'],
    contentSafety: {
      enabled: true,
      failPolicy: 'FailClosed',
      promptShieldsEnabled: true,
      checkInput: true,
      checkOutput: true,
      checkRetrievedKnowledge: true,
      checkToolResults: true,
      hateThreshold: 4,
      sexualThreshold: 4,
      violenceThreshold: 4,
      selfHarmThreshold: 4,
    },
    ...overrides,
  };
}

describe('GuardrailsConfig content-safety runtime panel', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('shows the content-safety panel with enabled badge and read-only toggles', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
      ok: true,
      json: async () => baseConfig(),
    } as Response);

    renderWithTheme(<GuardrailsConfig />);
    expect(await screen.findByTestId('content-safety-runtime-panel')).toBeInTheDocument();
    const badge = screen.getByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('data-safety-enabled', 'true');
    expect(screen.getByTestId('cs-check-input')).toHaveTextContent(/on/i);
    expect(screen.getByTestId('cs-check-output')).toHaveTextContent(/on/i);
    expect(screen.getByTestId('cs-check-retrieved-knowledge')).toHaveTextContent(/on/i);
    expect(screen.getByTestId('cs-check-tool-results')).toHaveTextContent(/on/i);
    expect(screen.getByTestId('cs-prompt-shields')).toHaveTextContent(/on/i);
  });

  it('shows a disabled badge and hides read-only toggles when content safety is off', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
      ok: true,
      json: async () => baseConfig({
        contentSafety: { ...baseConfig().contentSafety!, enabled: false },
      }),
    } as Response);

    renderWithTheme(<GuardrailsConfig />);
    const panel = await screen.findByTestId('content-safety-runtime-panel');
    const badge = screen.getByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('data-safety-enabled', 'false');
    expect(panel.textContent).toMatch(/pattern-based guardrails still apply/i);
    // Read-only rows are hidden when disabled so users can't infer runtime
    // config that isn't in effect.
    expect(screen.queryByTestId('cs-check-input')).not.toBeInTheDocument();
  });

  it('renders protection toggles from the backend config contract', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
      ok: true,
      json: async () => baseConfig({
        piiDetectionEnabled: true,
        jailbreakDetectionEnabled: true,
        autoRedactPii: false,
      }),
    } as Response);

    renderWithTheme(<GuardrailsConfig />);

    expect(await screen.findByLabelText('Toggle jailbreak detection')).toBeChecked();
    expect(screen.getByLabelText('Toggle PII detection')).toBeChecked();
    expect(screen.getByLabelText('Toggle auto-redact PII')).not.toBeChecked();
    expect(screen.queryByLabelText('Toggle access control')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Blocked patterns')).not.toBeInTheDocument();
  });

  it('saves with the backend contract and reloads the changed state', async () => {
    const user = userEvent.setup();
    let serverConfig = baseConfig({ jailbreakDetectionEnabled: true });
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === '/api/guardrails/config' && init?.method === 'PUT') {
        const payload = JSON.parse(String(init.body)) as Partial<GuardrailsConfigData> & {
          jailbreakEnabled?: boolean;
          piiEnabled?: boolean;
        };
        expect(payload).toHaveProperty('jailbreakDetectionEnabled', false);
        expect(payload).not.toHaveProperty('jailbreakEnabled');
        expect(payload).not.toHaveProperty('piiEnabled');
        serverConfig = { ...serverConfig, ...payload, status: 'updated' };
        return {
          ok: true,
          json: async () => serverConfig,
        } as Response;
      }
      if (url === '/api/guardrails/config') {
        return {
          ok: true,
          json: async () => serverConfig,
        } as Response;
      }
      throw new Error(`Unmocked fetch: ${url}`);
    });

    const first = renderWithTheme(<GuardrailsConfig />);
    const jailbreakSwitch = await screen.findByLabelText('Toggle jailbreak detection');
    expect(jailbreakSwitch).toBeChecked();

    await user.click(jailbreakSwitch);
    await user.click(screen.getByRole('button', { name: 'Save Configuration' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/guardrails/config', expect.objectContaining({ method: 'PUT' })));
    first.unmount();

    renderWithTheme(<GuardrailsConfig />);

    expect(await screen.findByLabelText('Toggle jailbreak detection')).not.toBeChecked();
  });
});

describe('GuardrailsConfig two-layer injection clarity', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  function mockConfig(overrides?: Partial<GuardrailsConfigData>) {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce({
      ok: true,
      json: async () => baseConfig(overrides),
    } as Response);
  }

  it('reworded jailbreak toggle scopes itself to the pattern layer only', async () => {
    mockConfig();
    renderWithTheme(<GuardrailsConfig />);

    // The visible label names the pattern layer explicitly, matching the
    // audit trail's PATTERN family so a user can connect toggle to rows.
    expect(await screen.findByText('🚫 Pattern-based jailbreak detection')).toBeInTheDocument();

    // The old copy claimed the toggle blocked all injection. It must not.
    expect(screen.queryByText('Block prompt injection and jailbreak attempts')).not.toBeInTheDocument();

    expect(screen.getByText(/this is only the pattern layer/i)).toBeInTheDocument();
    expect(screen.getByText(/not the whole injection defence/i)).toBeInTheDocument();
  });

  it('separates settings you control from deployment-managed runtime protections', async () => {
    mockConfig();
    renderWithTheme(<GuardrailsConfig />);

    const userSettings = await screen.findByTestId('user-configurable-settings');
    const runtimePanel = screen.getByTestId('content-safety-runtime-panel');

    // Two distinct containers: one the user controls, one the deployment owns.
    expect(userSettings).toBeInTheDocument();
    expect(runtimePanel).toBeInTheDocument();
    expect(userSettings).not.toContainElement(runtimePanel);

    expect(screen.getByTestId('deployment-managed-note').textContent)
      .toMatch(/managed by the deployment/i);

    // The user-controlled group holds the jailbreak toggle; the runtime panel does not.
    expect(userSettings).toContainElement(screen.getByLabelText('Toggle jailbreak detection'));
    expect(runtimePanel).toContainElement(screen.getByTestId('cs-prompt-shields'));
  });

  it('explains that both injection layers exist and names them like the audit trail', async () => {
    mockConfig();
    renderWithTheme(<GuardrailsConfig />);

    const explainer = await screen.findByTestId('injection-defense-explainer');
    // Vocabulary must match the audit rows (PATTERN, MODEL · PROMPT-SHIELD SAFETY).
    expect(screen.getByTestId('explainer-pattern-label')).toHaveTextContent('PATTERN');
    expect(screen.getByTestId('explainer-model-label')).toHaveTextContent('MODEL · PROMPT-SHIELD SAFETY');
    expect(explainer.textContent).toMatch(/prompt shields/i);
    expect(explainer.textContent).toMatch(/managed by the deployment/i);
  });

  it('states plainly that turning pattern detection off leaves Prompt Shields on', async () => {
    mockConfig();
    renderWithTheme(<GuardrailsConfig />);

    const note = await screen.findByTestId('pattern-off-still-shielded-note');
    expect(note.textContent).toMatch(/does not turn off prompt shields/i);
    expect(note.textContent).toMatch(/can still be blocked/i);
  });
});
