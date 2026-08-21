import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { KnowledgeIngestionBlock } from '../components/guardrails/KnowledgeIngestionBlock';
import { buildSafetyBlockDisplay } from '../utils/safetyDisplay';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('KnowledgeIngestionBlock', () => {
  it('renders with role="alert" and stage attribute', () => {
    renderWithTheme(<KnowledgeIngestionBlock documentTitle="quarterly-report.md" />);
    const el = screen.getByTestId('knowledge-ingestion-block');
    expect(el).toHaveAttribute('role', 'alert');
    expect(el).toHaveAttribute('data-safety-stage', 'ingestion');
  });

  it('shows the document title supplied by the user', () => {
    renderWithTheme(<KnowledgeIngestionBlock documentTitle="quarterly-report.md" />);
    expect(screen.getByTestId('knowledge-ingestion-title')).toHaveTextContent(
      'quarterly-report.md',
    );
  });

  it('renders category and severity chips when the display carries them', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'ingestion',
      detectionType: 'content-safety-violence',
      category: 'Violence',
      severity: 6,
      decision: 'Blocked',
    });
    renderWithTheme(
      <KnowledgeIngestionBlock documentTitle="doc.md" display={display} />,
    );
    expect(screen.getByTestId('knowledge-ingestion-category')).toHaveTextContent(/Violent content/);
    expect(screen.getByTestId('knowledge-ingestion-severity')).toHaveTextContent(/severe/i);
  });

  it('never leaks internal rule / threshold text', () => {
    const display = buildSafetyBlockDisplay({
      stage: 'ingestion',
      detectionType: 'content-safety-hate',
      category: 'Hate',
      severity: 4,
    });
    renderWithTheme(
      <KnowledgeIngestionBlock documentTitle="doc.md" display={display} />,
    );
    const el = screen.getByTestId('knowledge-ingestion-block');
    expect(el.textContent ?? '').not.toMatch(/RULE_ID_|THRESHOLD_|SENSITIVE_PATTERN_|content-safety-/i);
  });
});
