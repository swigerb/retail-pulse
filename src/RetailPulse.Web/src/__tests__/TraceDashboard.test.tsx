import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { TraceDashboard } from '../components/traces';
import type { Trace } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

function expectStatValue(label: string, value: string) {
  const stat = screen.getByText(label).parentElement;
  expect(stat).not.toBeNull();
  expect(within(stat!).getByText(value)).toBeInTheDocument();
}

describe('TraceDashboard', () => {
  it('shows empty state when no traces', () => {
    render(wrap(<TraceDashboard traces={[]} />));
    expect(screen.getByText('No traces recorded yet')).toBeInTheDocument();
  });

  it('computes duration and cost from span-level fallback when trace totals are 0', () => {
    const trace: Trace = {
      traceId: 'trace-1',
      intent: 'Processing...',
      agentName: 'Unknown',
      startTime: '2026-05-15T01:39:53Z',
      status: 'in_progress',
      totalDurationMs: 0,
      totalTokens: 0,
      totalCostUsd: 0,
      spans: [
        { id: 's1', name: 'GetHistoricalDemand', type: 'tool', startTime: '2026-05-15T01:39:53Z', durationMs: 30000, inputTokens: 500, outputTokens: 200, estimatedCostUsd: 0.004 },
        { id: 's2', name: 'GetSeasonalityFactors', type: 'tool', startTime: '2026-05-15T01:40:23Z', durationMs: 38000, inputTokens: 600, outputTokens: 300, estimatedCostUsd: 0.0032 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));

    // Should show trace count (may appear in multiple spots, use getAllByText)
    expect(screen.getAllByText('1').length).toBeGreaterThanOrEqual(1);
    // Should show avg duration computed from spans (68s)
    expect(screen.getAllByText('68.00s').length).toBeGreaterThanOrEqual(1);
    // Should show avg cost computed from spans ($0.0072)
    expect(screen.getAllByText('$0.0072').length).toBeGreaterThanOrEqual(1);
    // Should count unique tools
    expect(screen.getAllByText('2').length).toBeGreaterThanOrEqual(1);
  });

  it('counts distinct tool spans and renders tool usage distribution', () => {
    const trace: Trace = {
      traceId: 'trace-2',
      intent: 'Demand analysis',
      agentName: 'Demand Forecast Agent',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 5000,
      totalTokens: 1000,
      totalCostUsd: 0.005,
      spans: [
        { id: 's1', name: 'GetDemand', type: 'tool', startTime: '2026-05-15T01:39:53Z', durationMs: 2000 },
        { id: 's2', name: 'GetSupply', type: 'tool', startTime: '2026-05-15T01:39:55Z', durationMs: 1500 },
        { id: 's3', name: 'GetDemand', type: 'tool', startTime: '2026-05-15T01:39:57Z', durationMs: 1000 },
        { id: 's4', name: 'router.classify', type: 'routing', startTime: '2026-05-15T01:39:58Z', durationMs: 500 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));

    expectStatValue('Unique Tools', '2');
    expect(screen.getByText('Tool Usage Distribution')).toBeInTheDocument();
    expect(screen.getByText(/GetDemand \(2\)/)).toBeInTheDocument();
    expect(screen.getByText(/GetSupply \(1\)/)).toBeInTheDocument();
  });

  it('shows zero unique tools and hides distribution when no tool spans exist', () => {
    const trace: Trace = {
      traceId: 'trace-2b',
      intent: 'Demand analysis',
      agentName: 'Demand Forecast Agent',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 5000,
      totalTokens: 1000,
      totalCostUsd: 0.005,
      spans: [
        { id: 's1', name: 'router.classify', type: 'routing', startTime: '2026-05-15T01:39:53Z', durationMs: 1000 },
        { id: 's2', name: 'agent.general.process', type: 'agent', startTime: '2026-05-15T01:39:54Z', durationMs: 4000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));

    expectStatValue('Unique Tools', '0');
    expect(screen.queryByText('Tool Usage Distribution')).not.toBeInTheDocument();
  });

  it('counts tool_call span types as tools', () => {
    const trace: Trace = {
      traceId: 'trace-2c',
      intent: 'Demand analysis',
      agentName: 'Demand Forecast Agent',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 5000,
      totalTokens: 1000,
      totalCostUsd: 0.005,
      spans: [
        { id: 's1', name: 'GetDemand', type: 'tool_call' as any, startTime: '2026-05-15T01:39:53Z', durationMs: 2000 },
        { id: 's2', name: 'GetSupply', type: 'tool_call' as any, startTime: '2026-05-15T01:39:55Z', durationMs: 3000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));
    expectStatValue('Unique Tools', '2');
  });

  it('displays "Completed" instead of "Processing..." for completed traces', () => {
    const trace: Trace = {
      traceId: 'trace-3',
      intent: 'Processing...',
      agentName: 'Demand Forecast Agent',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 68000,
      totalTokens: 25400,
      totalCostUsd: 0.0072,
      spans: [
        { id: 's1', name: 'Agent', type: 'agent', startTime: '2026-05-15T01:39:53Z', durationMs: 68000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.queryByText('Processing...')).not.toBeInTheDocument();
  });

  it('derives agent name from spans when trace agentName is "Unknown"', () => {
    const trace: Trace = {
      traceId: 'trace-4',
      intent: 'Forecast query',
      agentName: 'Unknown',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 5000,
      totalTokens: 1000,
      totalCostUsd: 0.003,
      spans: [
        { id: 's1', name: 'Demand Forecast Agent', type: 'agent', startTime: '2026-05-15T01:39:53Z', durationMs: 5000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));
    expect(screen.getByText('Demand Forecast Agent')).toBeInTheDocument();
    expect(screen.queryByText('Unknown')).not.toBeInTheDocument();
  });

  it('shows checkmark badge for completed traces', () => {
    const trace: Trace = {
      traceId: 'trace-5',
      intent: 'Query',
      agentName: 'Agent',
      startTime: '2026-05-15T01:39:53Z',
      status: 'completed',
      totalDurationMs: 2000,
      totalTokens: 500,
      totalCostUsd: 0.001,
      spans: [
        { id: 's1', name: 'Op', type: 'routing', startTime: '2026-05-15T01:39:53Z', durationMs: 2000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));
    expect(screen.getByText('✓')).toBeInTheDocument();
  });
});
