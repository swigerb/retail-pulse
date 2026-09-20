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

  describe('final reply rendering (issue #297)', () => {
    it('renders markdown in finalReply through ReactMarkdown + remarkGfm (bold, headings)', () => {
      const md = '### Coastline Tacos\n\n**Northeast** outperforms *Midwest* by 12%.';
      render(
        wrap(
          <PlanView
            active={makeActive({ status: 'completed', finalReply: md })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      const container = screen.getByTestId('plan-final-reply');
      // Literal markdown syntax must not survive rendering.
      expect(container.textContent).not.toContain('###');
      expect(container.textContent).not.toContain('**');
      // Heading rendered as an actual <h3>.
      const heading = container.querySelector('h3');
      expect(heading?.textContent).toBe('Coastline Tacos');
      // Bold rendered as <strong>.
      const strong = container.querySelector('strong');
      expect(strong?.textContent).toBe('Northeast');
      // Container carries the shared markdown-body class matching PlanStepRow.
      expect(container.className).toMatch(/markdown-body/);
    });

    it('renders GFM tables in finalReply (remark-gfm wired up)', () => {
      const md = '| Region | Sales |\n| --- | --- |\n| NE | 100 |\n| MW | 80 |';
      render(
        wrap(
          <PlanView
            active={makeActive({ status: 'completed', finalReply: md })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      const container = screen.getByTestId('plan-final-reply');
      expect(container.querySelector('table')).not.toBeNull();
      expect(container.querySelectorAll('th')).toHaveLength(2);
      expect(container.querySelectorAll('tbody tr')).toHaveLength(2);
    });

    it('sanitizes tool-call artifacts out of finalReply before rendering', () => {
      const noisy =
        'Here is the real answer.\n\nto=functions.get_kpis {"region":"NE"}\n\n**Coastline** wins.';
      render(
        wrap(
          <PlanView
            active={makeActive({ status: 'completed', finalReply: noisy })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      const container = screen.getByTestId('plan-final-reply');
      expect(container.textContent).not.toContain('to=functions');
      expect(container.textContent).toContain('Here is the real answer.');
      expect(container.querySelector('strong')?.textContent).toBe('Coastline');
    });
  });

  describe('terminal reason gating (issue #298)', () => {
    it('hides terminalReason when the plan completed via ReviewerApproved', () => {
      render(
        wrap(
          <PlanView
            active={makeActive({
              status: 'completed',
              finalReply: 'All good.',
              terminalReason: 'PlanReviewApproved',
            })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      expect(screen.queryByTestId('plan-terminal-reason')).toBeNull();
      expect(screen.queryByText(/terminal reason/i)).toBeNull();
    });

    it('hides terminalReason when the plan completed via ReviewerEdited', () => {
      render(
        wrap(
          <PlanView
            active={makeActive({
              status: 'completed',
              finalReply: 'Edited then approved.',
              terminalReason: 'PlanReviewEdited',
            })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      expect(screen.queryByTestId('plan-terminal-reason')).toBeNull();
    });

    it('shows terminalReason for a genuine failure (PlanReviewReplanExhausted)', () => {
      render(
        wrap(
          <PlanView
            active={makeActive({
              status: 'failed',
              finalReply: 'We could not finish this plan.',
              terminalReason: 'PlanReviewReplanExhausted',
            })}
            connected={true}
            onApprove={vi.fn()}
            onReject={vi.fn()}
            onEdit={vi.fn()}
            onClarify={vi.fn()}
          />,
        ),
      );
      const banner = screen.getByTestId('plan-terminal-reason');
      expect(banner.textContent).toContain('PlanReviewReplanExhausted');
    });
  });
});

