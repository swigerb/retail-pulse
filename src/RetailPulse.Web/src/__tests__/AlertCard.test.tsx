import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { AlertCard } from '../components/alerts/AlertCard';
import type { Alert } from '../types';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

const baseAlert: Alert = {
  id: 'alert-1',
  title: 'Depletions dropped 15% in Southeast',
  severity: 'high',
  brand: 'Apex Grill',
  region: 'Southeast',
  changePercent: -15.3,
  description: 'Weekly depletions for Apex Grill in the Southeast region have dropped 15.3% compared to the previous period.',
  recommendedAction: 'Review distributor inventory levels and schedule a call with the regional sales manager.',
  firedAt: new Date().toISOString(),
  status: 'active',
};

describe('AlertCard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders alert card with all sections', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    expect(screen.getByTestId('alert-card')).toBeInTheDocument();
    expect(screen.getByText(/Depletions dropped 15%/)).toBeInTheDocument();
    expect(screen.getByText(/Weekly depletions for Apex Grill/)).toBeInTheDocument();
    expect(screen.getByTestId('severity-badge')).toHaveTextContent('High');
  });

  it('shows severity badge with correct color coding for each level', () => {
    const { rerender } = render(wrap(<AlertCard alert={{ ...baseAlert, severity: 'high' }} autoDismissMs={0} />));
    expect(screen.getByTestId('severity-badge')).toHaveTextContent('High');

    rerender(wrap(<AlertCard alert={{ ...baseAlert, severity: 'medium' }} autoDismissMs={0} />));
    expect(screen.getByTestId('severity-badge')).toHaveTextContent('Medium');

    rerender(wrap(<AlertCard alert={{ ...baseAlert, severity: 'low' }} autoDismissMs={0} />));
    expect(screen.getByTestId('severity-badge')).toHaveTextContent('Low');
  });

  it('shows brand and region context tags', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    expect(screen.getAllByText(/Apex Grill/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Southeast/).length).toBeGreaterThan(0);
  });

  it('shows negative change percent in red direction', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    expect(screen.getAllByText(/15\.3%/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/↓/).length).toBeGreaterThan(0);
  });

  it('expands to show details on View Details click', async () => {
    const user = userEvent.setup();
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    expect(screen.queryByTestId('alert-details')).not.toBeInTheDocument();
    await user.click(screen.getByText('View Details'));
    expect(screen.getByTestId('alert-details')).toBeInTheDocument();
    expect(screen.getByText(/Review distributor inventory/)).toBeInTheDocument();
  });

  it('calls onDismiss when dismiss button is clicked', async () => {
    const onDismiss = vi.fn();
    const user = userEvent.setup();
    render(wrap(<AlertCard alert={baseAlert} onDismiss={onDismiss} autoDismissMs={0} />));
    await user.click(screen.getByLabelText('Dismiss alert'));
    expect(onDismiss).toHaveBeenCalledWith('alert-1');
  });

  it('opens snooze menu and calls onSnooze with selected duration', async () => {
    const onSnooze = vi.fn();
    const user = userEvent.setup();
    render(wrap(<AlertCard alert={baseAlert} onSnooze={onSnooze} autoDismissMs={0} />));

    await user.click(screen.getByText('Snooze'));
    expect(screen.getByTestId('snooze-menu')).toBeInTheDocument();

    await user.click(screen.getByText('4 hours'));
    expect(onSnooze).toHaveBeenCalledWith('alert-1', '4h');
  });

  it('renders all four snooze options', async () => {
    const user = userEvent.setup();
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    await user.click(screen.getByText('Snooze'));
    expect(screen.getByText('1 hour')).toBeInTheDocument();
    expect(screen.getByText('4 hours')).toBeInTheDocument();
    expect(screen.getByText('24 hours')).toBeInTheDocument();
    expect(screen.getByText('1 week')).toBeInTheDocument();
  });

  it('has proper ARIA role and label', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    const card = screen.getByRole('alert');
    expect(card).toHaveAttribute('aria-label', 'high severity alert: Depletions dropped 15% in Southeast');
  });

  it('shows auto-dismiss progress bar when not interacted', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={30000} />));
    expect(screen.getByTestId('auto-dismiss-bar')).toBeInTheDocument();
  });

  it('hides auto-dismiss bar when autoDismissMs is 0', () => {
    render(wrap(<AlertCard alert={baseAlert} autoDismissMs={0} />));
    expect(screen.queryByTestId('auto-dismiss-bar')).not.toBeInTheDocument();
  });
});
