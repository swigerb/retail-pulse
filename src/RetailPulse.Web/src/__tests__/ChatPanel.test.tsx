import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';

// --- Mocks -----------------------------------------------------------------
// Stable mocks for the API + telemetry hub so ChatPanel can mount cleanly in
// jsdom without making real network calls or opening SignalR sockets.
const sendMessageMock = vi.fn();
const isErrorReplyMock = vi.fn((reply: string) => reply.startsWith('⏳'));

vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => isErrorReplyMock(reply),
}));

vi.mock('../services/telemetryHub', () => ({
  joinTelemetrySession: vi.fn(),
  onProgress: vi.fn(() => () => {}),
}));

// ChartRenderer is lazy-loaded; mock so Suspense resolves synchronously and
// no Recharts work runs in the test env.
vi.mock('../components/ChartRenderer', () => ({
  default: () => <div data-testid="chart-renderer-mock" />,
}));

// jsdom doesn't implement scrollIntoView — ChatPanel calls it after every
// render. Stub it once for the suite.
beforeEach(() => {
  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = vi.fn();
  } else {
    (Element.prototype.scrollIntoView as unknown as ReturnType<typeof vi.fn>) = vi.fn();
  }
  sendMessageMock.mockReset();
  isErrorReplyMock.mockClear();
  isErrorReplyMock.mockImplementation((reply: string) => reply.startsWith('⏳'));
});

// Import AFTER vi.mock so the mocked modules are used.
import { ChatPanel } from '../components/ChatPanel';

function renderPanel(props: Partial<React.ComponentProps<typeof ChatPanel>> = {}) {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <ChatPanel {...props} />
    </FluentProvider>,
  );
}

describe('ChatPanel', () => {
  it('renders welcome screen with suggested prompt chips on mount', () => {
    renderPanel();

    expect(screen.getByText(/Welcome to Retail Pulse/i)).toBeInTheDocument();

    // The All category chip + every PROMPT_CATEGORIES label should be visible.
    expect(screen.getByRole('button', { name: /🏪 All/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /📊 General Retail/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /🛒 Grocery/i })).toBeInTheDocument();

    // At least one suggested-prompt button is rendered (one per category in
    // the default "All" view).
    expect(
      screen.getByRole('button', { name: /Compare depletion trends across all regions/i }),
    ).toBeInTheDocument();
  });

  it('sends a message via the API and renders both user + assistant bubbles', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      reply: 'Sales were up 12% in Q1.',
      sessionId: 'sess-1',
      spans: [],
      totalDurationMs: 1234,
    });

    renderPanel();

    const input = screen.getByPlaceholderText(/Ask about retail performance/i);
    await user.type(input, 'How are sales?');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // User bubble appears immediately.
    expect(await screen.findByText('How are sales?')).toBeInTheDocument();

    // API was invoked with the right payload.
    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    const [request] = sendMessageMock.mock.calls[0];
    expect(request).toMatchObject({ message: 'How are sales?' });
    expect(typeof request.sessionId).toBe('string');

    // Assistant reply renders once the promise resolves.
    expect(await screen.findByText(/Sales were up 12% in Q1\./)).toBeInTheDocument();
  });

  it('shows a loading indicator while a request is in flight and clears it on completion', async () => {
    const user = userEvent.setup();
    let resolveSend: ((v: unknown) => void) | undefined;
    sendMessageMock.mockImplementation(
      () => new Promise((resolve) => {
        resolveSend = resolve;
      }),
    );

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Hi');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // Loading state: ProgressIndicator becomes visible.
    expect(await screen.findByTestId('progress-indicator')).toBeInTheDocument();

    // Resolve the request and confirm the loading UI tears down.
    resolveSend?.({
      reply: 'Hello back!',
      sessionId: 'sess-x',
      spans: [],
      totalDurationMs: 50,
    });

    await waitFor(() => {
      expect(screen.queryByTestId('progress-indicator')).not.toBeInTheDocument();
    });
    expect(screen.getByText(/Hello back!/)).toBeInTheDocument();
  });

  it('renders an Error: bubble when the API rejects', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockRejectedValue(new Error('network exploded'));

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'broken');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText(/Error: network exploded/)).toBeInTheDocument();

    // Spinner cleared — input is editable again.
    await waitFor(() => {
      expect(screen.queryByTestId('progress-indicator')).not.toBeInTheDocument();
    });
  });

  it('suppresses routing metadata when the reply is a backend error (200 OK)', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      reply: '⏳ The AI service is busy. Please wait a moment and try again.',
      sessionId: 'sess-err',
      spans: [],
      totalDurationMs: 100,
      routing: {
        selectedAgent: 'Demand Forecast Agent',
        confidence: 0.78,
        agentScores: {},
      },
    });

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'forecast?');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // Error text renders…
    expect(await screen.findByText(/The AI service is busy/i)).toBeInTheDocument();

    // …but the misleading routing label must NOT be displayed.
    expect(screen.queryByText(/Demand Forecast Agent/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/78%/)).not.toBeInTheDocument();
  });
});
