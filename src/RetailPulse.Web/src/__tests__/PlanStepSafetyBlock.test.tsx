import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { PlanStepSafetyBlock } from '../components/guardrails/PlanStepSafetyBlock';
import { buildSafetyBlockDisplay } from '../utils/safetyDisplay';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('PlanStepSafetyBlock', () => {
  it('renders the step ordinal, intent and action', () => {
    renderWithTheme(
      <PlanStepSafetyBlock stepIndex={2} intent="Summarize" action="Summarize the Q3 outlook" />,
    );
    const container = screen.getByTestId('plan-step-safety-block');
    expect(container).toHaveAttribute('data-plan-step-index', '2');
    expect(container.textContent).toMatch(/Step 3 blocked/);
    expect(container.textContent).toMatch(/Summarize the Q3 outlook/);
  });

  it('renders a plan-preserved note by default', () => {
    renderWithTheme(<PlanStepSafetyBlock stepIndex={0} intent="Read" action="Fetch data" />);
    expect(screen.getByTestId('plan-preserved-note')).toHaveTextContent(/plan continues/i);
  });

  it('suppresses the plan-preserved note when planPreserved=false', () => {
    renderWithTheme(
      <PlanStepSafetyBlock stepIndex={0} intent="Read" action="Fetch data" planPreserved={false} />,
    );
    expect(screen.queryByTestId('plan-preserved-note')).not.toBeInTheDocument();
  });

  it('renders category / severity from the display model', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'plan-step',
      detectionType: 'content-safety-selfharm',
      category: 'SelfHarm',
      severity: 4,
    });
    renderWithTheme(
      <PlanStepSafetyBlock stepIndex={1} intent="Explain" action="Explain topic" display={display} />,
    );
    expect(screen.getByTestId('plan-step-category')).toHaveTextContent(/Self-harm content/);
    expect(screen.getByTestId('plan-step-severity')).toHaveTextContent(/high/i);
  });

  it('never leaks internal pattern/threshold/rule text', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'plan-step',
      detectionType: 'content-safety-violence',
      category: 'Violence',
      severity: 6,
    });
    renderWithTheme(
      <PlanStepSafetyBlock stepIndex={0} intent="Test" action="Test action" display={display} />,
    );
    const container = screen.getByTestId('plan-step-safety-block');
    expect(container.textContent ?? '').not.toMatch(/RULE_ID_|THRESHOLD_|SENSITIVE_PATTERN_|content-safety-/i);
  });
});
