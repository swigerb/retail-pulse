import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { PlanView } from '../components/plan/PlanView';
import type { ActivePlanState } from '../state/planReducer';
import type { PlanStep } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

function makeStep(index: number, status: PlanStep['status'], key = 'demand-forecasting'): PlanStep {
  return {
    stepId: `s-${index}`,
    planId: 'p1',
    stepIndex: index,
    specialistKey: key,
    intent: 'demand',
    action: `run step ${index}`,
    status,
  };
}

function makeActive(overrides: Partial<ActivePlanState> = {}): ActivePlanState {
  return {
    planId: 'p1',
    sessionId: 'sess',
    request: 'compare Brand X and Y in NE',
    status: 'running',
    steps: [makeStep(0, 'completed'), makeStep(1, 'running'), makeStep(2, 'pending')],
    detectedIntents: ['demand', 'promo'],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    elapsedMs: 8_500,
    startedAt: Date.now() - 8_500,
    ...overrides,
  };
}

describe('PlanView', () => {
  it('renders the plan header, progress, elapsed and step list', () => {
    render(
      wrap(
        <PlanView
          active={makeActive()}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    const panel = screen.getByTestId('plan-view');
    expect(panel).toHaveAttribute('data-plan-status', 'running');
    expect(screen.getByTestId('plan-status-pill').textContent).toContain('Running');
    expect(screen.getByTestId('plan-elapsed').textContent).toContain('8.5s');
    expect(screen.getByTestId('plan-progress-value').textContent).toContain('1 / 3');
    expect(screen.getAllByTestId('plan-step-row')).toHaveLength(3);
  });

  it('shows the connection warning when disconnected', () => {
    render(
      wrap(
        <PlanView
          active={makeActive()}
          connected={false}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-connection-warning')).toBeInTheDocument();
  });

  it('renders the review card when a review is pending', () => {
    render(
      wrap(
        <PlanView
          active={makeActive({
            status: 'awaiting_review',
            review: {
              requestId: 'req-1',
              round: 0,
              proposal: {
                planId: 'p1',
                roundNumber: 0,
                request: 'q',
                steps: [{ specialistKey: 'demand-forecasting', intent: 'demand', action: 'go' }],
                revisionReason: null,
              },
            },
          })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-review-card')).toBeInTheDocument();
  });

  it('renders the clarification card when awaiting clarification', () => {
    render(
      wrap(
        <PlanView
          active={makeActive({
            status: 'awaiting_clarification',
            clarification: {
              requestId: 'req-2',
              prompt: {
                planId: 'p1',
                stepIndex: 1,
                specialistKey: 'demand-forecasting',
                question: 'which region?',
              },
            },
          })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-clarification-card')).toBeInTheDocument();
    expect(screen.getByText(/which region/i)).toBeInTheDocument();
  });

  it('renders the final reply when the plan settles', () => {
    render(
      wrap(
        <PlanView
          active={makeActive({ status: 'completed', finalReply: 'here is the aggregate' })}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-final-reply').textContent).toContain('here is the aggregate');
  });

  it('accessible plan status pill carries a role and label', () => {
    render(
      wrap(
        <PlanView
          active={makeActive()}
          connected={true}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
          onClarify={vi.fn()}
        />,
      ),
    );
    const pill = screen.getByTestId('plan-status-pill');
    expect(pill).toHaveAttribute('role', 'status');
    expect(pill).toHaveAttribute('aria-label', expect.stringContaining('Plan status'));
  });
});
