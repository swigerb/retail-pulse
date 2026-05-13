import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AgentRoutingIndicator } from '../components/AgentRoutingIndicator';
import type { RoutingInfo } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

const baseRouting: RoutingInfo = {
  agentId: 'demand-001',
  agentName: 'Demand Agent',
  intentCategory: 'demand',
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

  it('shows reasoning when pill is clicked', () => {
    const routing: RoutingInfo = {
      ...baseRouting,
      reasoning: 'User asked about depletion trends which maps to demand forecasting.',
    };
    renderWithTheme(<AgentRoutingIndicator routing={routing} />);

    expect(screen.queryByText(routing.reasoning!)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByText(routing.reasoning!)).toBeInTheDocument();
  });

  it('hides reasoning on second click', () => {
    const routing: RoutingInfo = {
      ...baseRouting,
      reasoning: 'Demand intent detected.',
    };
    renderWithTheme(<AgentRoutingIndicator routing={routing} />);

    fireEvent.click(screen.getByRole('button'));
    expect(screen.getByText(routing.reasoning!)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button'));
    expect(screen.queryByText(routing.reasoning!)).not.toBeInTheDocument();
  });

  it('renders different colors for different intent categories', () => {
    const sentimentRouting: RoutingInfo = {
      agentId: 'sentiment-001',
      agentName: 'Sentiment Agent',
      intentCategory: 'sentiment',
      confidence: 0.87,
    };
    renderWithTheme(<AgentRoutingIndicator routing={sentimentRouting} />);
    expect(screen.getByText('Sentiment Agent')).toBeInTheDocument();
    expect(screen.getByText('💬')).toBeInTheDocument();
    expect(screen.getByText('87%')).toBeInTheDocument();
  });
});
