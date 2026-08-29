import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import CostDashboard from '../components/observability/CostDashboard';
import { fetchCostDashboard } from '../services/observabilityApi';
import type { CostDashboardData } from '../types';

// Mock recharts to avoid canvas issues in jsdom
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children }: { children: React.ReactNode }) => <div data-testid="responsive-container">{children}</div>,
  LineChart: ({ children }: { children: React.ReactNode }) => <div data-testid="line-chart">{children}</div>,
  Line: () => <div data-testid="line" />,
  AreaChart: ({ children }: { children: React.ReactNode }) => <div data-testid="area-chart">{children}</div>,
  Area: () => <div data-testid="area" />,
  BarChart: ({ children }: { children: React.ReactNode }) => <div data-testid="bar-chart">{children}</div>,
  Bar: () => <div data-testid="bar" />,
  XAxis: () => <div />,
  YAxis: () => <div />,
  Tooltip: () => <div />,
  CartesianGrid: () => <div />,
  Cell: () => <div />,
}));

const mockData: CostDashboardData = {
  summary: {
    totalTokens: 125000,
    totalCost: 4.50,
    requestCount: 87,
    avgCostPerRequest: 0.052,
  },
  trend: [
    { date: '2026-05-10', cost: 1.20, tokens: 32000, requests: 25 },
    { date: '2026-05-11', cost: 1.50, tokens: 40000, requests: 30 },
    { date: '2026-05-12', cost: 1.80, tokens: 53000, requests: 32 },
  ],
  agentBreakdown: [
    { agentName: 'Demand Agent', totalTokens: 50000, totalCost: 1.80, requestCount: 35 },
    { agentName: 'Supply Agent', totalTokens: 35000, totalCost: 1.20, requestCount: 25 },
    { agentName: 'General Agent', totalTokens: 40000, totalCost: 1.50, requestCount: 27 },
  ],
  topTools: [
    { toolName: 'GetDepletionStats', callCount: 42, totalDurationMs: 35700, avgDurationMs: 850 },
    { toolName: 'CreateChart', callCount: 28, totalDurationMs: 840, avgDurationMs: 30 },
  ],
};

vi.mock('../services/observabilityApi', () => ({
  fetchCostDashboard: vi.fn(() => Promise.resolve(mockData)),
}));

const wrap = (ui: React.ReactNode) => (
  <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>
);

describe('CostDashboard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(fetchCostDashboard).mockResolvedValue(mockData);
  });

  it('renders the cost dashboard container', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByTestId('cost-dashboard')).toBeInTheDocument();
    });
  });

  it('shows period selector tabs', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByText('Today')).toBeInTheDocument();
      expect(screen.getByText('This Week')).toBeInTheDocument();
      expect(screen.getByText('This Month')).toBeInTheDocument();
    });
  });

  it('displays summary metric cards after loading', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByText('125,000')).toBeInTheDocument();
      expect(screen.getByText('$4.50')).toBeInTheDocument();
      expect(screen.getByText('87')).toBeInTheDocument();
    });
  });

  it('renders charts after data loads', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByTestId('tools-table')).toBeInTheDocument();
    });
  });

  it('renders top tools table', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByText('GetDepletionStats')).toBeInTheDocument();
      expect(screen.getByText('CreateChart')).toBeInTheDocument();
    });
  });

  // Tool spans are MCP round trips and carry no model tokens, so the table reports time in
  // tool. A token column here could only ever read zero.
  it('reports time in tool rather than tokens', async () => {
    render(wrap(<CostDashboard />));
    const table = await screen.findByTestId('tools-table');
    expect(within(table).getAllByRole('columnheader').map(h => h.textContent))
      .toEqual(['Tool', 'Calls', 'Total Time', 'Avg Duration']);
    expect(within(table).getByText('35.7s')).toBeInTheDocument();
    expect(within(table).getByText('840ms')).toBeInTheDocument();
  });

  it('renders friendly empty states for idle chart sections', async () => {
    vi.mocked(fetchCostDashboard).mockResolvedValueOnce({
      summary: {
        totalTokens: 0,
        totalCost: 0,
        requestCount: 0,
        avgCostPerRequest: 0,
      },
      trend: [],
      agentBreakdown: [],
      topTools: [],
    });

    render(wrap(<CostDashboard />));

    await waitFor(() => {
      expect(screen.getByTestId('trend-empty')).toHaveTextContent('No data yet — start a chat to see activity.');
      expect(screen.getByTestId('agent-breakdown-empty')).toHaveTextContent('No data yet — start a chat to see activity.');
      expect(screen.getByTestId('tools-empty')).toHaveTextContent('No data yet — start a chat to see activity.');
    });
  });

  it('treats zero-filled trend buckets as empty without hiding active sections', async () => {
    vi.mocked(fetchCostDashboard).mockResolvedValueOnce({
      ...mockData,
      trend: [
        { date: 'Jun 24', cost: 0, tokens: 0 },
        { date: 'Jun 25', cost: 0, tokens: 0 },
        { date: 'Jun 26', cost: 0, tokens: 0 },
      ],
    });

    render(wrap(<CostDashboard />));

    await waitFor(() => {
      expect(screen.getByTestId('trend-empty')).toHaveTextContent('No data yet — start a chat to see activity.');
      expect(screen.queryByTestId('area-chart')).not.toBeInTheDocument();
      expect(screen.getByTestId('bar-chart')).toBeInTheDocument();
      expect(screen.getByTestId('tools-table')).toBeInTheDocument();
    });
  });

  it('changes period when tab is clicked', async () => {
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByText('Today')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByText('This Week'));
    await waitFor(() => {
      expect(fetchCostDashboard).toHaveBeenCalledWith('week', expect.any(AbortSignal));
    });
  });
});
