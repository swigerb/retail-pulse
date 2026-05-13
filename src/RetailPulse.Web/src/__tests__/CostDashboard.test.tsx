import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import CostDashboard from '../components/observability/CostDashboard';
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
    { toolName: 'GetDepletionStats', callCount: 42, totalTokens: 30000, avgDurationMs: 850 },
    { toolName: 'CreateChart', callCount: 28, totalTokens: 20000, avgDurationMs: 1200 },
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

  it('changes period when tab is clicked', async () => {
    const { fetchCostDashboard } = await import('../services/observabilityApi');
    render(wrap(<CostDashboard />));
    await waitFor(() => {
      expect(screen.getByText('Today')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByText('This Week'));
    await waitFor(() => {
      expect(fetchCostDashboard).toHaveBeenCalledWith('week');
    });
  });
});
