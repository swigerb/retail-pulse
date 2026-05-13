import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { ApprovalCard } from '../components/ApprovalCard';
import type { ApprovalRequest } from '../types';

// Mock the approval API
vi.mock('../services/approvalApi', () => ({
  respondToApproval: vi.fn().mockResolvedValue(undefined),
}));

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const baseApproval: ApprovalRequest = {
  id: 'apr-1',
  action: 'Reorder 500 units of Brand X for Southeast region',
  reasoning: 'Stock levels below safety threshold; demand forecast shows 20% increase next month.',
  impact: 'Estimated $12,500 order — within auto-approve budget threshold.',
  urgency: 'high',
  agentId: 'supply-agent',
  agentName: 'Supply Chain Agent',
  requestedAt: new Date().toISOString(),
  timeoutAt: new Date(Date.now() + 300_000).toISOString(), // 5 min from now
  status: 'pending',
};

describe('ApprovalCard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders pending approval card with all sections', () => {
    render(wrap(<ApprovalCard approval={baseApproval} />));
    expect(screen.getByTestId('approval-card')).toBeInTheDocument();
    expect(screen.getByText(/Reorder 500 units/)).toBeInTheDocument();
    expect(screen.getByText(/Stock levels below safety threshold/)).toBeInTheDocument();
    expect(screen.getByText(/Estimated \$12,500/)).toBeInTheDocument();
    expect(screen.getByTestId('urgency-badge')).toHaveTextContent('High');
  });

  it('shows urgency badge with correct color coding', () => {
    const { rerender } = render(wrap(<ApprovalCard approval={{ ...baseApproval, urgency: 'high' }} />));
    expect(screen.getByTestId('urgency-badge')).toHaveTextContent('High');

    rerender(wrap(<ApprovalCard approval={{ ...baseApproval, urgency: 'medium' }} />));
    expect(screen.getByTestId('urgency-badge')).toHaveTextContent('Medium');

    rerender(wrap(<ApprovalCard approval={{ ...baseApproval, urgency: 'low' }} />));
    expect(screen.getByTestId('urgency-badge')).toHaveTextContent('Low');
  });

  it('shows countdown timer for pending approval', () => {
    render(wrap(<ApprovalCard approval={baseApproval} />));
    const timer = screen.getByTestId('approval-timer');
    expect(timer).toBeInTheDocument();
    // Timer should show something like "4:59" or similar
    expect(timer.textContent).toMatch(/\d+:\d{2}/);
  });

  it('renders approve, reject, and modify buttons', () => {
    render(wrap(<ApprovalCard approval={baseApproval} />));
    expect(screen.getByTestId('approve-button')).toBeInTheDocument();
    expect(screen.getByTestId('reject-button')).toBeInTheDocument();
    expect(screen.getByTestId('modify-button')).toBeInTheDocument();
  });

  it('calls respondToApproval when approve is clicked', async () => {
    const { respondToApproval } = await import('../services/approvalApi');
    const onResolved = vi.fn();
    render(wrap(<ApprovalCard approval={baseApproval} onResolved={onResolved} />));

    await userEvent.click(screen.getByTestId('approve-button'));

    await waitFor(() => {
      expect(respondToApproval).toHaveBeenCalledWith('apr-1', {
        decision: 'approved',
        comment: undefined,
      });
    });
    expect(onResolved).toHaveBeenCalledWith('apr-1', 'approved');
  });

  it('calls respondToApproval when reject is clicked', async () => {
    const { respondToApproval } = await import('../services/approvalApi');
    const onResolved = vi.fn();
    render(wrap(<ApprovalCard approval={baseApproval} onResolved={onResolved} />));

    await userEvent.click(screen.getByTestId('reject-button'));

    await waitFor(() => {
      expect(respondToApproval).toHaveBeenCalledWith('apr-1', {
        decision: 'rejected',
        comment: undefined,
      });
    });
    expect(onResolved).toHaveBeenCalledWith('apr-1', 'rejected');
  });

  it('shows comment field when modify is clicked', async () => {
    render(wrap(<ApprovalCard approval={baseApproval} />));
    await userEvent.click(screen.getByTestId('modify-button'));
    expect(screen.getByTestId('approval-comment')).toBeInTheDocument();
  });

  it('renders resolved state with decision banner', () => {
    const resolved: ApprovalRequest = {
      ...baseApproval,
      status: 'approved',
      decidedBy: 'Brian Swiger',
      decidedAt: new Date().toISOString(),
    };
    render(wrap(<ApprovalCard approval={resolved} />));
    expect(screen.getByTestId('resolved-banner')).toBeInTheDocument();
    expect(screen.getByText('Approved')).toBeInTheDocument();
    expect(screen.getByText(/Brian Swiger/)).toBeInTheDocument();
    // Buttons should not be visible
    expect(screen.queryByTestId('approve-button')).not.toBeInTheDocument();
  });

  it('renders timed out state', () => {
    const timedOut: ApprovalRequest = {
      ...baseApproval,
      status: 'timed_out',
      timeoutAt: new Date(Date.now() - 1000).toISOString(),
    };
    render(wrap(<ApprovalCard approval={timedOut} />));
    expect(screen.getByTestId('resolved-banner')).toBeInTheDocument();
    expect(screen.getByText('Timed Out')).toBeInTheDocument();
  });

  it('disables buttons while responding', async () => {
    // Make respondToApproval hang
    const { respondToApproval } = await import('../services/approvalApi');
    (respondToApproval as ReturnType<typeof vi.fn>).mockImplementation(() => new Promise(() => {}));

    render(wrap(<ApprovalCard approval={baseApproval} />));
    fireEvent.click(screen.getByTestId('approve-button'));

    await waitFor(() => {
      expect(screen.getByTestId('approve-button')).toBeDisabled();
      expect(screen.getByTestId('reject-button')).toBeDisabled();
    });
  });
});
