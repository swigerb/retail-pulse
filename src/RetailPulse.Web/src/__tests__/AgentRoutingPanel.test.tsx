import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AgentRoutingPanel } from '../components/AgentRoutingPanel';
import type { RoutingInfo } from '../types';

function renderWithTheme(ui: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>);
}

describe('AgentRoutingPanel', () => {
  it('renders empty state when no routing history', () => {
    renderWithTheme(<AgentRoutingPanel routingHistory={[]} />);
    expect(screen.getByText('🔀 Agent Routing')).toBeInTheDocument();
    expect(screen.getByText(/routing statistics will appear/i)).toBeInTheDocument();
  });

  it('renders stats when routing history is provided', () => {
    const history: RoutingInfo[] = [
      { agentId: 'd1', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.9 },
      { agentId: 'd2', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.8 },
      { agentId: 'g1', agentName: 'General Agent', intentCategory: 'general', confidence: 0.5 },
    ];
    renderWithTheme(<AgentRoutingPanel routingHistory={history} />);

    expect(screen.getByText('3')).toBeInTheDocument(); // total queries
    expect(screen.getByText('73%')).toBeInTheDocument(); // avg confidence (0.9+0.8+0.5)/3 = 0.733 -> 73%
    expect(screen.getByText('33%')).toBeInTheDocument(); // fallback rate 1/3
  });

  it('renders bar chart with agent categories', () => {
    const history: RoutingInfo[] = [
      { agentId: 'd1', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.95 },
      { agentId: 's1', agentName: 'Sentiment Agent', intentCategory: 'sentiment', confidence: 0.88 },
    ];
    renderWithTheme(<AgentRoutingPanel routingHistory={history} />);

    expect(screen.getByText('Demand')).toBeInTheDocument();
    expect(screen.getByText('Sentiment')).toBeInTheDocument();
  });

  it('sorts agents by query count descending', () => {
    const history: RoutingInfo[] = [
      { agentId: 's1', agentName: 'Sentiment Agent', intentCategory: 'sentiment', confidence: 0.9 },
      { agentId: 'd1', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.85 },
      { agentId: 'd2', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.92 },
      { agentId: 'd3', agentName: 'Demand Agent', intentCategory: 'demand', confidence: 0.88 },
    ];
    renderWithTheme(<AgentRoutingPanel routingHistory={history} />);

    const labels = screen.getAllByText(/Demand|Sentiment/);
    expect(labels[0].textContent).toContain('Demand');
    expect(labels[1].textContent).toContain('Sentiment');
  });
});
