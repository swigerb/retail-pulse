import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { WithheldOutputMessage } from '../components/guardrails/WithheldOutputMessage';
import { buildSafetyBlockDisplay } from '../utils/safetyDisplay';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('WithheldOutputMessage', () => {
  it('renders with role="status" and a plain-language default reason', () => {
    renderWithTheme(<WithheldOutputMessage />);
    const el = screen.getByTestId('withheld-output-message');
    expect(el).toHaveAttribute('role', 'status');
    expect(el).toHaveAttribute('data-safety-stage', 'output');
    expect(el.textContent).toMatch(/withheld/i);
  });

  it('renders category and severity chips when the display carries them', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'output',
      detectionType: 'content-safety-violence',
      category: 'Violence',
      severity: 4,
      decision: 'Blocked',
    });
    renderWithTheme(<WithheldOutputMessage display={display} />);
    expect(screen.getByTestId('withheld-output-category')).toHaveTextContent(/Violent content/);
    expect(screen.getByTestId('withheld-output-severity')).toHaveTextContent(/high/i);
  });

  it('never surfaces internal detection-type slugs', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'output',
      detectionType: 'content-safety-hate',
      category: 'Hate',
      severity: 6,
    });
    renderWithTheme(<WithheldOutputMessage display={display} />);
    const el = screen.getByTestId('withheld-output-message');
    expect(el.textContent ?? '').not.toMatch(/content-safety-/i);
    expect(el.textContent ?? '').not.toMatch(/RULE_ID_|THRESHOLD_|SENSITIVE_PATTERN_/i);
  });
});
