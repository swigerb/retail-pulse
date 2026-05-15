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
});
