import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { TraceCard } from '../components/traces/TraceCard';
import type { Trace } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const mockTrace: Trace = {
  traceId: 'trace-1',
  intent: 'Query depletions',
  agentName: 'Demand Agent',
  startTime: '2026-05-13T12:00:00Z',
  totalDurationMs: 1800,
  totalTokens: 2500,
  totalCostUsd: 0.003,
  status: 'completed',
  spans: [
    {
      id: 'span-1',
      name: 'Route Intent',
      type: 'routing',
      startTime: '2026-05-13T12:00:00.000Z',
      durationMs: 100,
    },
    {
      id: 'span-2',
      parentId: 'span-1',
      name: 'Demand Agent',
      type: 'agent',
      startTime: '2026-05-13T12:00:00.100Z',
      durationMs: 1200,
    },
    {
      id: 'span-3',
      parentId: 'span-2',
      name: 'GetPortfolioDepletionStats',
      type: 'tool',
      startTime: '2026-05-13T12:00:00.300Z',
      durationMs: 400,
    },
  ],
};

describe('TraceCard', () => {
  it('renders compact summary line', () => {
    render(wrap(<TraceCard trace={mockTrace} />));
    expect(screen.getByTestId('trace-card')).toBeInTheDocument();
    // 1.8s · 1 tool · $0.003
    expect(screen.getByText(/1\.8s/)).toBeInTheDocument();
    expect(screen.getByText(/1 tool/)).toBeInTheDocument();
    expect(screen.getByText(/\$0\.003/)).toBeInTheDocument();
  });

  it('is collapsed by default', () => {
    render(wrap(<TraceCard trace={mockTrace} />));
    expect(screen.queryByTestId('trace-card-expanded')).not.toBeInTheDocument();
  });

  it('expands on click to show step-by-step breakdown', async () => {
    const user = userEvent.setup();
    render(wrap(<TraceCard trace={mockTrace} />));
    await user.click(screen.getByLabelText('How I got this answer'));
    expect(screen.getByTestId('trace-card-expanded')).toBeInTheDocument();
    // Span names appear in both steps list and timeline — check they exist
    expect(screen.getAllByText('Route Intent').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Demand Agent').length).toBeGreaterThan(0);
    expect(screen.getAllByText('GetPortfolioDepletionStats').length).toBeGreaterThan(0);
  });

  it('collapses again on second click', async () => {
    const user = userEvent.setup();
    render(wrap(<TraceCard trace={mockTrace} />));
    await user.click(screen.getByLabelText('How I got this answer'));
    expect(screen.getByTestId('trace-card-expanded')).toBeInTheDocument();
    await user.click(screen.getByLabelText('How I got this answer'));
    expect(screen.queryByTestId('trace-card-expanded')).not.toBeInTheDocument();
  });

  it('shows correct tool count for multiple tools', () => {
    const multiToolTrace: Trace = {
      ...mockTrace,
      spans: [
        ...mockTrace.spans,
        { id: 'span-4', parentId: 'span-2', name: 'CreateChart', type: 'tool', startTime: '2026-05-13T12:00:01.000Z', durationMs: 200 },
      ],
    };
    render(wrap(<TraceCard trace={multiToolTrace} />));
    expect(screen.getByText(/2 tools/)).toBeInTheDocument();
  });

  it('has proper aria-expanded attribute', async () => {
    const user = userEvent.setup();
    render(wrap(<TraceCard trace={mockTrace} />));
    const btn = screen.getByLabelText('How I got this answer');
    expect(btn).toHaveAttribute('aria-expanded', 'false');
    await user.click(btn);
    expect(btn).toHaveAttribute('aria-expanded', 'true');
  });
});
