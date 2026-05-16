import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { TraceDashboard } from '../components/traces';
import type { Trace } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
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

  it('counts tool_call span types as tools', () => {
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
        { id: 's1', name: 'GetDemand', type: 'tool_call' as any, startTime: '2026-05-15T01:39:53Z', durationMs: 2000 },
        { id: 's2', name: 'GetSupply', type: 'tool_call' as any, startTime: '2026-05-15T01:39:55Z', durationMs: 3000 },
      ],
    };

    render(wrap(<TraceDashboard traces={[trace]} />));
    // 2 unique tools should be counted (appears in stats and possibly other places)
    expect(screen.getAllByText('2').length).toBeGreaterThanOrEqual(1);
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
