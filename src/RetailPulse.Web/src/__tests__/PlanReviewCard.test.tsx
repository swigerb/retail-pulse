import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { PlanReviewCard } from '../components/plan/PlanReviewCard';
import type { PlanReviewStep } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const steps: PlanReviewStep[] = [
  { specialistKey: 'demand-forecasting', intent: 'demand', action: 'forecast Brand X' },
  { specialistKey: 'promo-planning', intent: 'promo', action: 'evaluate a display promo' },
];

describe('PlanReviewCard', () => {
  it('renders proposed steps and shows the round label', () => {
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="brand X in NE"
          steps={steps}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-review-card')).toBeInTheDocument();
    expect(screen.getByTestId('plan-review-round').textContent).toContain('Round 1');
    const list = screen.getByTestId('plan-review-steps');
    expect(list.textContent).toContain('demand-forecasting');
    expect(list.textContent).toContain('promo-planning');
  });

  it('invokes onApprove with an optional comment', async () => {
    const onApprove = vi.fn();
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          onApprove={onApprove}
          onReject={vi.fn()}
          onEdit={vi.fn()}
        />,
      ),
    );
    const comment = screen.getByTestId('plan-review-comment');
    await userEvent.type(comment, 'looks good');
    await userEvent.click(screen.getByTestId('plan-review-approve'));
    expect(onApprove).toHaveBeenCalledWith('looks good');
  });

  it('reject requires feedback before submitting', async () => {
    const onReject = vi.fn();
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          onApprove={vi.fn()}
          onReject={onReject}
          onEdit={vi.fn()}
        />,
      ),
    );
    await userEvent.click(screen.getByTestId('plan-review-reject'));
    const submit = screen.getByTestId('plan-review-submit-reject');
    expect(submit).toBeDisabled();
    await userEvent.type(screen.getByTestId('plan-review-feedback'), 'no promos in NE');
    expect(submit).not.toBeDisabled();
    await userEvent.click(submit);
    expect(onReject).toHaveBeenCalledWith('no promos in NE');
  });

  it('edit removes a step and submits the amended plan', async () => {
    const onEdit = vi.fn();
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={onEdit}
        />,
      ),
    );
    await userEvent.click(screen.getByTestId('plan-review-edit'));
    fireEvent.click(screen.getByTestId('plan-review-remove-1'));
    fireEvent.click(screen.getByTestId('plan-review-submit-edit'));
    expect(onEdit).toHaveBeenCalledTimes(1);
    const submitted = onEdit.mock.calls[0][0] as PlanReviewStep[];
    expect(submitted).toHaveLength(1);
    expect(submitted[0].specialistKey).toBe('demand-forecasting');
  });

  it('disables all decision controls while a decision is in flight', () => {
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          decisionInFlight="approve"
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-review-approve')).toBeDisabled();
    expect(screen.getByTestId('plan-review-reject')).toBeDisabled();
    expect(screen.getByTestId('plan-review-edit')).toBeDisabled();
  });

  it('shows the resolved banner and hides action buttons once decided', () => {
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          resolvedKind="approve"
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
        />,
      ),
    );
    expect(screen.getByTestId('plan-review-resolved')).toBeInTheDocument();
    expect(screen.queryByTestId('plan-review-approve')).not.toBeInTheDocument();
  });

  it('carries screen-reader labels on every decision control', () => {
    render(
      wrap(
        <PlanReviewCard
          planId="p1"
          requestId="req-1"
          round={0}
          request="q"
          steps={steps}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdit={vi.fn()}
        />,
      ),
    );
    expect(screen.getByLabelText(/approve plan/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/reject plan/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/edit plan/i)).toBeInTheDocument();
  });
});
