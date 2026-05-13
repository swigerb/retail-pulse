import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { TraceTimeline } from '../components/traces/TraceTimeline';
import type { Trace } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const mockTrace: Trace = {
  traceId: 'trace-1',
  intent: 'What are Apex Grill depletions in the Southeast?',
  agentName: 'Demand Agent',
  startTime: '2026-05-13T12:00:00Z',
  totalDurationMs: 2400,
  totalTokens: 3200,
  totalCostUsd: 0.0045,
  status: 'completed',
  spans: [
    {
      id: 'span-1',
      name: 'Route Intent',
      type: 'routing',
      startTime: '2026-05-13T12:00:00.000Z',
      durationMs: 120,
      inputTokens: 150,
      outputTokens: 30,
      estimatedCostUsd: 0.0003,
    },
    {
      id: 'span-2',
      parentId: 'span-1',
      name: 'Demand Agent',
      type: 'agent',
      startTime: '2026-05-13T12:00:00.120Z',
      durationMs: 1800,
      inputTokens: 1200,
      outputTokens: 800,
      estimatedCostUsd: 0.003,
    },
    {
      id: 'span-3',
      parentId: 'span-2',
      name: 'GetPortfolioDepletionStats',
      type: 'tool',
      startTime: '2026-05-13T12:00:00.300Z',
      durationMs: 450,
      attributes: { brand: 'Apex Grill', region: 'Southeast' },
    },
    {
      id: 'span-4',
      parentId: 'span-2',
      name: 'Memory Recall',
      type: 'memory',
      startTime: '2026-05-13T12:00:01.000Z',
      durationMs: 80,
      inputTokens: 50,
      outputTokens: 20,
    },
  ],
};

describe('TraceTimeline', () => {
  it('renders trace timeline with all spans', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByTestId('trace-timeline')).toBeInTheDocument();
    const rows = screen.getAllByTestId('trace-span-row');
    expect(rows).toHaveLength(4);
  });

  it('shows trace intent and status in header', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByText(/Apex Grill depletions/)).toBeInTheDocument();
    expect(screen.getByText('completed')).toBeInTheDocument();
  });

  it('shows agent name and duration in header stats', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getAllByText(/Demand Agent/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/2\.40s/).length).toBeGreaterThan(0);
  });

  it('renders waterfall bars for each span', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    const bars = screen.getAllByTestId('trace-span-bar');
    expect(bars).toHaveLength(4);
  });

  it('shows span names in label column', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByText('Route Intent')).toBeInTheDocument();
    expect(screen.getByText('Demand Agent')).toBeInTheDocument();
    expect(screen.getByText('GetPortfolioDepletionStats')).toBeInTheDocument();
    expect(screen.getByText('Memory Recall')).toBeInTheDocument();
  });

  it('shows total cost in footer', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByText(/\$0\.004/)).toBeInTheDocument();
  });

  it('shows legend with all span types', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByText('routing')).toBeInTheDocument();
    expect(screen.getByText('agent')).toBeInTheDocument();
    expect(screen.getByText('tool')).toBeInTheDocument();
    expect(screen.getByText('memory')).toBeInTheDocument();
    expect(screen.getByText('approval')).toBeInTheDocument();
  });

  it('shows empty state when trace has no spans', () => {
    const emptyTrace: Trace = { ...mockTrace, spans: [] };
    render(wrap(<TraceTimeline trace={emptyTrace} />));
    expect(screen.getByText(/No spans recorded/)).toBeInTheDocument();
  });

  it('shows span count and duration in footer', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    expect(screen.getByText(/4 spans/)).toBeInTheDocument();
  });

  it('renders token counts for spans that have them', () => {
    render(wrap(<TraceTimeline trace={mockTrace} />));
    // Span-1: 150+30=180, Span-2: 1200+800=2000 (2.0K), Span-4: 50+20=70
    expect(screen.getByText(/2\.0K/)).toBeInTheDocument();
  });
});
