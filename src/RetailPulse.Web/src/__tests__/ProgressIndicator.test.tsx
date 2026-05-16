import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { ProgressIndicator } from '../components/ProgressIndicator';
import type { ProgressStep } from '../components/ProgressIndicator';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('ProgressIndicator', () => {
  it('renders current phase with pulsing indicator', () => {
    renderWithTheme(
      <ProgressIndicator currentPhase="Calling GetHistoricalDemand..." completedSteps={[]} />
    );
    expect(screen.getByTestId('progress-indicator')).toBeInTheDocument();
    expect(screen.getByTestId('progress-current-phase')).toHaveTextContent('Calling GetHistoricalDemand...');
  });

  it('renders completed steps with checkmarks', () => {
    const steps: ProgressStep[] = [
      { phase: 'tool_result', detail: 'GetHistoricalDemand completed', durationMs: 45, timestamp: '2024-01-01T00:00:00Z' },
      { phase: 'tool_result', detail: 'GetInventoryLevels completed', durationMs: 120, timestamp: '2024-01-01T00:00:01Z' },
    ];
    renderWithTheme(
      <ProgressIndicator currentPhase="Synthesizing response..." completedSteps={steps} />
    );
    const completedElements = screen.getAllByTestId('progress-completed-step');
    expect(completedElements).toHaveLength(2);
    expect(completedElements[0]).toHaveTextContent('GetHistoricalDemand completed');
    expect(completedElements[0]).toHaveTextContent('45ms');
    expect(completedElements[1]).toHaveTextContent('GetInventoryLevels completed');
    expect(completedElements[1]).toHaveTextContent('120ms');
  });

  it('does not show duration when not provided', () => {
    const steps: ProgressStep[] = [
      { phase: 'tool_result', detail: 'Step done', timestamp: '2024-01-01T00:00:00Z' },
    ];
    renderWithTheme(
      <ProgressIndicator currentPhase="Working..." completedSteps={steps} />
    );
    const step = screen.getByTestId('progress-completed-step');
    expect(step).toHaveTextContent('Step done');
    expect(step).not.toHaveTextContent('ms');
  });

  it('renders empty completed steps with only current phase', () => {
    renderWithTheme(
      <ProgressIndicator currentPhase="Thinking..." completedSteps={[]} />
    );
    expect(screen.queryAllByTestId('progress-completed-step')).toHaveLength(0);
    expect(screen.getByTestId('progress-current-phase')).toHaveTextContent('Thinking...');
  });
});
