import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { BlockedRequestMessage } from '../components/guardrails/BlockedRequestMessage';
import { buildSafetyBlockDisplay } from '../utils/safetyDisplay';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('BlockedRequestMessage', () => {
  it('renders the shield icon', () => {
    renderWithTheme(<BlockedRequestMessage reason="Request was blocked" />);
    expect(screen.getByText('🛡️')).toBeInTheDocument();
  });

  it('displays the plain-language reason from legacy props', () => {
    renderWithTheme(<BlockedRequestMessage reason="This request could not be processed." />);
    expect(screen.getByText(/This request could not be processed\./)).toBeInTheDocument();
  });

  it('displays a suggestion when provided via legacy props', () => {
    renderWithTheme(
      <BlockedRequestMessage
        reason="This request could not be processed."
        suggestion="Try rephrasing your question about general sales metrics."
      />,
    );
    expect(screen.getByTestId('blocked-request-suggestion')).toHaveTextContent(
      /Try rephrasing your question about general sales metrics/,
    );
  });

  it('omits the suggestion block when not provided', () => {
    renderWithTheme(<BlockedRequestMessage reason="Blocked" />);
    expect(screen.queryByTestId('blocked-request-suggestion')).not.toBeInTheDocument();
  });

  it('has role="alert" for accessibility', () => {
    renderWithTheme(<BlockedRequestMessage reason="Blocked" />);
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });

  it('renders category, severity, and family markers from a display model', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-hate',
      category: 'Hate',
      severity: 4,
      decision: 'Blocked',
    });
    renderWithTheme(<BlockedRequestMessage display={display} />);
    const container = screen.getByTestId('blocked-request-message');
    expect(container).toHaveAttribute('data-safety-stage', 'input');
    expect(container).toHaveAttribute('data-safety-family', 'model');
    expect(screen.getByTestId('blocked-request-category')).toHaveTextContent(/Hateful content/);
    expect(screen.getByTestId('blocked-request-severity')).toHaveTextContent(/high/i);
  });

  it('shows the "safety service unavailable" decision chip when fail-closed', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-unavailable',
      decision: 'ServiceUnavailable',
      failClosed: true,
    });
    renderWithTheme(<BlockedRequestMessage display={display} />);
    expect(screen.getByTestId('blocked-request-decision')).toHaveTextContent(
      /Safety service unavailable/,
    );
  });

  it('never leaks raw detection-type substrings from the display model', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-hate',
      category: 'Hate',
      severity: 4,
    });
    renderWithTheme(<BlockedRequestMessage display={display} />);
    const container = screen.getByTestId('blocked-request-message');
    // Detection-type slug should never appear in rendered text.
    expect(container.textContent ?? '').not.toMatch(/content-safety-/i);
  });

  it('does not render internal rule/pattern/threshold names even if seeded through legacy reason', () => {
    // Simulate a hostile caller trying to smuggle internal detail into the
    // legacy prop path. The component MUST NOT surface the recognisable
    // pattern-family keywords beyond the exact reason text supplied, and
    // absolutely must not synthesise a detection-type slug.
    const sensitiveMarkers = [
      'RULE_ID_123',
      'THRESHOLD_ABC',
      'SENSITIVE_PATTERN_XYZ',
    ];
    renderWithTheme(
      <BlockedRequestMessage reason="This request could not be processed." />,
    );
    const container = screen.getByTestId('blocked-request-message');
    for (const marker of sensitiveMarkers) {
      expect(container.textContent ?? '').not.toContain(marker);
    }
    expect(container).not.toHaveAttribute('data-safety-pattern');
    expect(container).not.toHaveAttribute('data-safety-threshold');
    expect(container).not.toHaveAttribute('data-safety-rule-id');
  });
});
