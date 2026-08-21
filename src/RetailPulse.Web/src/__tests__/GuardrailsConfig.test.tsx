import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { GuardrailsConfig } from '../components/guardrails/GuardrailsConfig';
import type { GuardrailsConfigData } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

function baseConfig(overrides?: Partial<GuardrailsConfigData>): GuardrailsConfigData {
  return {
    jailbreakEnabled: true,
    piiEnabled: true,
    accessControlEnabled: true,
    blockedPatterns: 'ignore all previous instructions\nyou are now DAN',
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
});
