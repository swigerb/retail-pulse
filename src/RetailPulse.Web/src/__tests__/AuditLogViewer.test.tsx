import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import AuditLogViewer from '../components/observability/AuditLogViewer';
import type { AuditLogPage } from '../types';

const mockPage: AuditLogPage = {
  entries: [
    {
      id: 'log-1',
      timestamp: '2026-05-13T10:00:00Z',
      userId: 'user-1',
      userName: 'Alice',
      agentName: 'Demand Agent',
      action: 'chat',
      inputSummary: 'What are the demand trends for Apex Grill?',
      outputSummary: 'Demand is up 8.2% YoY across all regions...',
      tokens: 2500,
      durationMs: 1200,
    },
    {
      id: 'log-2',
      timestamp: '2026-05-13T10:05:00Z',
      userId: 'user-2',
      userName: 'Bob',
      agentName: 'Supply Agent',
      action: 'tool_call',
      inputSummary: 'Check inventory levels for Southeast',
      outputSummary: 'Inventory at 3.2 weeks, fill rate 96%...',
      tokens: 1800,
      durationMs: 800,
    },
  ],
  totalCount: 2,
  page: 1,
  pageSize: 50,
};

vi.mock('../services/observabilityApi', () => ({
  fetchAuditLog: vi.fn(() => Promise.resolve(mockPage)),
}));

const wrap = (ui: React.ReactNode) => (
  <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>
);

describe('AuditLogViewer', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the audit log viewer container', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByTestId('audit-log-viewer')).toBeInTheDocument();
    });
  });

  it('shows log entries after loading', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument();
      expect(screen.getByText('Bob')).toBeInTheDocument();
    });
  });

  it('shows agent names in log entries', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByText('Demand Agent')).toBeInTheDocument();
      expect(screen.getByText('Supply Agent')).toBeInTheDocument();
    });
  });

  it('shows action type in entries', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByText('chat')).toBeInTheDocument();
      expect(screen.getByText('tool_call')).toBeInTheDocument();
    });
  });

  it('expands a row to show detail on click', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument();
    });
    // Click the first row to expand
    const rows = screen.getAllByTestId(/^audit-row-/);
    fireEvent.click(rows[0]);
    await waitFor(() => {
      expect(screen.getByText(/What are the demand trends/i)).toBeInTheDocument();
    });
  });

  it('renders filter controls', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByTestId('filter-search')).toBeInTheDocument();
    });
  });

  it('shows pagination info', async () => {
    render(wrap(<AuditLogViewer />));
    await waitFor(() => {
      expect(screen.getByTestId('audit-pagination')).toBeInTheDocument();
    });
  });
});
