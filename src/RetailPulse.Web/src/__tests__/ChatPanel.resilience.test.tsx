import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';

// --- Mocks -----------------------------------------------------------------
const sendMessageMock = vi.fn();
const isErrorReplyMock = vi.fn((reply: string) => reply.startsWith('⏳'));
const cancelChatSessionMock = vi.fn().mockResolvedValue('cancelled');
const cancelPlanMock = vi.fn().mockResolvedValue('cancelled');

// `vi.mock` factories are hoisted above module-level `class` declarations, so
// a plain top-level class would be in its temporal dead zone when the
// `../services/api` factory below evaluates the `ChatRequestTimeoutError`
// shorthand. `vi.hoisted` runs alongside the hoisted mocks, guaranteeing the
// class binding exists before the factory runs.
const { ChatRequestTimeoutError } = vi.hoisted(() => {
  class ChatRequestTimeoutError extends Error {
    readonly name = 'ChatRequestTimeoutError';
    readonly timeoutMs: number;
    constructor(timeoutMs: number) {
      super('Request timed out');
      this.timeoutMs = timeoutMs;
    }
  }
  return { ChatRequestTimeoutError };
});

vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => isErrorReplyMock(reply),
  isChatRequestTimeoutError: (err: unknown) => err instanceof ChatRequestTimeoutError,
  ChatRequestTimeoutError,
}));

vi.mock('../services/executionControlApi', () => ({
  cancelChatSession: (...args: unknown[]) => cancelChatSessionMock(...args),
  cancelPlan: (...args: unknown[]) => cancelPlanMock(...args),
}));

vi.mock('../services/telemetryHub', () => ({
  joinTelemetrySession: vi.fn(),
  onProgress: vi.fn(() => () => {}),
  onHubConnectionStatus: vi.fn((listener: (s: string) => void) => {
    // Fire an initial connected status so the indicator has something to
    // render deterministically. Return a no-op unsubscribe.
    listener('connected');
    return () => {};
  }),
  onHubHeartbeat: vi.fn(() => () => {}),
  getHubConnectionStatus: () => 'connected',
  getLastHubHeartbeatAt: () => null,
}));

vi.mock('../auth/activeProvider', () => ({
  activeAuthMode: 'entra',
}));

vi.mock('../components/ChartRenderer', () => ({
  default: () => <div data-testid="chart-renderer-mock" />,
}));

beforeEach(() => {
  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = vi.fn();
  } else {
    (Element.prototype.scrollIntoView as unknown as ReturnType<typeof vi.fn>) = vi.fn();
  }
  sendMessageMock.mockReset();
  cancelChatSessionMock.mockClear();
  cancelChatSessionMock.mockResolvedValue('cancelled');
  cancelPlanMock.mockClear();
  cancelPlanMock.mockResolvedValue('cancelled');
  isErrorReplyMock.mockClear();
  isErrorReplyMock.mockImplementation((reply: string) => reply.startsWith('⏳'));
});

// Import AFTER vi.mock so the mocked modules are used.
import { ChatPanel } from '../components/ChatPanel';

function renderPanel(props: { telemetryOpen?: boolean } = {}) {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <ChatPanel {...props} />
    </FluentProvider>,
  );
}

describe('ChatPanel resilience surface (issue #92)', () => {
  it('renders the ConnectionStatusIndicator inside the composer', () => {
    renderPanel();
    const indicator = screen.getByTestId('connection-status-indicator');
    expect(indicator).toBeInTheDocument();
    expect(indicator).toHaveAttribute('data-status', 'connected');
  });

  // The composer pill and the Real-Time Telemetry drawer badge report the SAME
  // SignalR connection. Rendering both states one fact twice, so the composer
  // pill yields to the drawer badge whenever the drawer is open.
  it('hides the composer connection pill while the telemetry drawer is open', () => {
    renderPanel({ telemetryOpen: true });
    expect(screen.queryByTestId('connection-status-indicator')).not.toBeInTheDocument();
  });

  it('restores the composer connection pill when the telemetry drawer closes', () => {
    const { rerender } = renderPanel({ telemetryOpen: true });
    expect(screen.queryByTestId('connection-status-indicator')).not.toBeInTheDocument();

    rerender(
      <FluentProvider theme={teamsDarkTheme}>
        <ChatPanel telemetryOpen={false} />
      </FluentProvider>,
    );
    expect(screen.getByTestId('connection-status-indicator')).toBeInTheDocument();
  });

  it('shows a visible Cancel button while a run is in flight and hides Send', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockImplementation(() => new Promise(() => {})); // never resolves

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'take a while');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    const cancel = await screen.findByTestId('chat-cancel-button');
    expect(cancel).toBeInTheDocument();
    // Send button is replaced by Cancel while loading.
    expect(screen.queryByRole('button', { name: /Send message/i })).not.toBeInTheDocument();
  });

  it('invokes cancelChatSession and clears loading when Cancel is pressed', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockImplementation(() => new Promise(() => {}));

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'question');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await user.click(await screen.findByTestId('chat-cancel-button'));

    await waitFor(() => expect(cancelChatSessionMock).toHaveBeenCalledTimes(1));
    // The chat sessionId must be a non-empty string — ChatPanel mints its own.
    expect(typeof cancelChatSessionMock.mock.calls[0][0]).toBe('string');
    expect((cancelChatSessionMock.mock.calls[0][0] as string).length).toBeGreaterThan(0);

    // Loading state cleared → the Send button is back.
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Send message/i })).toBeInTheDocument(),
    );
    // A "Cancelled." bubble is rendered so the user sees the outcome.
    expect(screen.getByText('Cancelled.')).toBeInTheDocument();
  });

  it('opens the timeout dialog on ChatRequestTimeoutError instead of an error bubble', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockRejectedValueOnce(new ChatRequestTimeoutError(90_000));

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'timeout me');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByTestId('timeout-dialog')).toBeInTheDocument();
    // No "Error:" bubble should have been appended — the dialog IS the feedback.
    expect(screen.queryByText(/^Error:/)).not.toBeInTheDocument();
  });

  it('Retry from the timeout dialog re-sends the same prompt', async () => {
    const user = userEvent.setup();
    sendMessageMock
      .mockRejectedValueOnce(new ChatRequestTimeoutError(90_000))
      .mockResolvedValueOnce({
        // Must match the actual `SendMessageResult` union returned by
        // `sendMessage` — the fast path is `{ kind: 'complete', response }`.
        // Returning a bare `ChatResponse` here would leave `result.response`
        // undefined in ChatPanel and blow up with
        // "Cannot read properties of undefined (reading 'reply')".
        kind: 'complete',
        response: {
          reply: 'second try works',
          sessionId: 'sess-retry',
          spans: [],
          totalDurationMs: 10,
        },
      });

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'do it again');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await user.click(await screen.findByTestId('timeout-dialog-retry'));

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(2));
    expect(sendMessageMock.mock.calls[1][0]).toMatchObject({ message: 'do it again' });
    expect(await screen.findByText('second try works')).toBeInTheDocument();
  });

  it('Abandon closes the dialog and restores an editable composer', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockRejectedValueOnce(new ChatRequestTimeoutError(90_000));

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'abandon me');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await user.click(await screen.findByTestId('timeout-dialog-abandon'));

    await waitFor(() =>
      expect(screen.queryByTestId('timeout-dialog')).not.toBeInTheDocument(),
    );
    // The Send button is back so the composer is editable.
    expect(screen.getByRole('button', { name: /Send message/i })).toBeInTheDocument();
    // Only the initial call — no retry was issued.
    expect(sendMessageMock).toHaveBeenCalledTimes(1);
  });
});
