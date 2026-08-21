import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AgentRoutingIndicator } from '../components/AgentRoutingIndicator';
import type { RoutingInfo } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

const baseRouting: RoutingInfo = {
  agentKey: 'demand-forecasting',
  agentName: 'Demand Agent',
  intent: 'demand/forecasting',
  confidence: 0.94,
};

describe('AgentRoutingIndicator', () => {
  it('renders agent name and confidence percentage', () => {
    renderWithTheme(<AgentRoutingIndicator routing={baseRouting} />);
    expect(screen.getByText('Demand Agent')).toBeInTheDocument();
    expect(screen.getByText('94%')).toBeInTheDocument();
  });

  it('renders the correct emoji for intent category', () => {
    renderWithTheme(<AgentRoutingIndicator routing={baseRouting} />);
    expect(screen.getByText('📈')).toBeInTheDocument();
  });

  it('renders different colors for different intent categories', () => {
    const sentimentRouting: RoutingInfo = {
      agentKey: 'field-sentiment',
      agentName: 'Sentiment Agent',
      intent: 'sentiment/analysis',
      confidence: 0.87,
    };
    renderWithTheme(<AgentRoutingIndicator routing={sentimentRouting} />);
    expect(screen.getByText('Sentiment Agent')).toBeInTheDocument();
    expect(screen.getByText('💬')).toBeInTheDocument();
    expect(screen.getByText('87%')).toBeInTheDocument();
  });

  it('omits the execution-path pill when routing has no executionPath (pre-#95 payload)', () => {
    renderWithTheme(<AgentRoutingIndicator routing={baseRouting} />);
    expect(screen.queryByTestId('execution-path-pill')).not.toBeInTheDocument();
  });

  it.each([
    ['fast', 'Fast'] as const,
    ['plan', 'Plan'] as const,
    ['council', 'Council'] as const,
  ])('renders the %s execution-path pill without a forced indicator', (path, label) => {
    renderWithTheme(
      <AgentRoutingIndicator routing={{ ...baseRouting, executionPath: path }} />,
    );
    const pill = screen.getByTestId('execution-path-pill');
    expect(pill).toHaveAttribute('data-execution-path', path);
    expect(pill).toHaveAttribute('data-execution-path-forced', 'false');
    expect(pill).toHaveTextContent(label);
    expect(pill).toHaveAttribute('aria-label', `Execution path: ${label}`);
  });

  it('marks the pill as forced when executionPathForced is true', () => {
    renderWithTheme(
      <AgentRoutingIndicator
        routing={{ ...baseRouting, executionPath: 'plan', executionPathForced: true }}
      />,
    );
    const pill = screen.getByTestId('execution-path-pill');
    expect(pill).toHaveAttribute('data-execution-path', 'plan');
    expect(pill).toHaveAttribute('data-execution-path-forced', 'true');
    expect(pill).toHaveAttribute('aria-label', 'Execution path: Plan (forced)');
  });

  it('does not mark the pill as forced when executionPathForced is explicitly false', () => {
    renderWithTheme(
      <AgentRoutingIndicator
        routing={{ ...baseRouting, executionPath: 'fast', executionPathForced: false }}
      />,
    );
    const pill = screen.getByTestId('execution-path-pill');
    expect(pill).toHaveAttribute('data-execution-path-forced', 'false');
    expect(pill).toHaveAttribute('aria-label', 'Execution path: Fast');
  });
});
