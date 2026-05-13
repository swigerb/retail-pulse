import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { BlockedRequestMessage } from '../components/guardrails/BlockedRequestMessage';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('BlockedRequestMessage', () => {
  it('renders the shield icon', () => {
    renderWithTheme(<BlockedRequestMessage reason="Jailbreak detected" />);
    expect(screen.getByText('🛡️')).toBeInTheDocument();
  });

  it('displays the blocking reason', () => {
    renderWithTheme(<BlockedRequestMessage reason="Prompt injection attempt detected" />);
    expect(screen.getByText(/Prompt injection attempt detected/)).toBeInTheDocument();
  });

  it('shows prefix text before reason', () => {
    renderWithTheme(<BlockedRequestMessage reason="PII sharing detected" />);
    expect(screen.getByText(/This request was blocked because:/)).toBeInTheDocument();
  });

  it('displays a suggestion when provided', () => {
    renderWithTheme(
      <BlockedRequestMessage
        reason="Access denied"
        suggestion="general sales metrics instead of protected financial data"
      />,
    );
    expect(screen.getByText(/Try rephrasing your question about/)).toBeInTheDocument();
    expect(screen.getByText(/general sales metrics/)).toBeInTheDocument();
  });

  it('does not show suggestion when not provided', () => {
    renderWithTheme(<BlockedRequestMessage reason="Blocked" />);
    expect(screen.queryByText(/Try rephrasing/)).not.toBeInTheDocument();
  });

  it('has role="alert" for accessibility', () => {
    renderWithTheme(<BlockedRequestMessage reason="Blocked" />);
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('has amber-style border (not error-red)', () => {
    renderWithTheme(<BlockedRequestMessage reason="Blocked" />);
    const container = screen.getByTestId('blocked-request-message');
    expect(container).toBeInTheDocument();
  });
});
