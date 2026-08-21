import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { TimeoutDialog } from '../components/TimeoutDialog';

function renderWith(open: boolean, onRetry: () => void, onAbandon: () => void, timeoutMs = 180_000) {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <TimeoutDialog open={open} timeoutMs={timeoutMs} onRetry={onRetry} onAbandon={onAbandon} />
    </FluentProvider>,
  );
}

describe('TimeoutDialog', () => {
  it('is not rendered while closed', () => {
    renderWith(false, vi.fn(), vi.fn());
    expect(screen.queryByTestId('timeout-dialog')).not.toBeInTheDocument();
  });

  it('renders the timeout duration in a human-readable way', () => {
    renderWith(true, vi.fn(), vi.fn(), 180_000);
    expect(screen.getByText(/180\s?seconds/)).toBeInTheDocument();
  });

  it('invokes onRetry when the retry button is pressed', async () => {
    const onRetry = vi.fn();
    renderWith(true, onRetry, vi.fn());
    await userEvent.click(screen.getByTestId('timeout-dialog-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('invokes onAbandon when the abandon button is pressed', async () => {
    const onAbandon = vi.fn();
    renderWith(true, vi.fn(), onAbandon);
    await userEvent.click(screen.getByTestId('timeout-dialog-abandon'));
    expect(onAbandon).toHaveBeenCalledTimes(1);
  });
});
