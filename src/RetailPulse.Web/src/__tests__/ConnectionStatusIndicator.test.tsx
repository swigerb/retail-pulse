import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { ConnectionStatusIndicator } from '../components/ConnectionStatusIndicator';

function renderWith(node: React.ReactElement) {
  return render(<FluentProvider theme={teamsDarkTheme}>{node}</FluentProvider>);
}

describe('ConnectionStatusIndicator', () => {
  it('renders a live label when connected', () => {
    renderWith(<ConnectionStatusIndicator status="connected" />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('data-status', 'connected');
    expect(indicator).toHaveAttribute('aria-label', 'Real-time channel: Live');
  });

  it('renders a connecting label during initial handshake', () => {
    renderWith(<ConnectionStatusIndicator status="connecting" />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('data-status', 'connecting');
    expect(screen.getByText(/Connecting/)).toBeInTheDocument();
  });

  it('renders a reconnecting label during transport retries', () => {
    renderWith(<ConnectionStatusIndicator status="reconnecting" />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('data-status', 'reconnecting');
  });

  it('renders a disconnected label after the retry budget is exhausted', () => {
    renderWith(<ConnectionStatusIndicator status="disconnected" />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('data-status', 'disconnected');
    expect(screen.getByText(/Disconnected/)).toBeInTheDocument();
  });

  it('presents a stalled connected channel as reconnecting', () => {
    renderWith(<ConnectionStatusIndicator status="connected" stalled={true} />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('data-status', 'reconnecting');
  });

  it('publishes live-region semantics for assistive tech', () => {
    renderWith(<ConnectionStatusIndicator status="reconnecting" />);
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toHaveAttribute('role', 'status');
    expect(indicator).toHaveAttribute('aria-live', 'polite');
  });
});
