import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { ContentSafetyStatusBadge } from '../components/guardrails/ContentSafetyStatusBadge';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('ContentSafetyStatusBadge', () => {
  it('renders the enabled state with role=status and text + icon', () => {
    renderWithTheme(<ContentSafetyStatusBadge enabled={true} failPolicy="FailClosed" />);
    const badge = screen.getByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('role', 'status');
    expect(badge).toHaveAttribute('data-safety-enabled', 'true');
    expect(badge.getAttribute('aria-label') ?? '').toMatch(/enabled.*FailClosed/i);
    expect(badge.textContent).toMatch(/Content safety enabled/);
    expect(badge.textContent).toMatch(/Fail policy: FailClosed/);
  });

  it('renders the disabled state clearly with text (not colour alone)', () => {
    renderWithTheme(<ContentSafetyStatusBadge enabled={false} />);
    const badge = screen.getByTestId('content-safety-status-badge');
    expect(badge).toHaveAttribute('data-safety-enabled', 'false');
    expect(badge.getAttribute('aria-label') ?? '').toMatch(/disabled/i);
    expect(badge.textContent).toMatch(/Content safety disabled/);
  });
});
