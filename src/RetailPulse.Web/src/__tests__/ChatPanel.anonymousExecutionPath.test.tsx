import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';

// This suite verifies that anonymous mode (where the backend ignores an
// override) does not present a misleading execution-path control. It lives
// in its own file so we can override the `activeAuthMode` mock cleanly.
const sendMessageMock = vi.fn();

vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => reply.startsWith('⏳'),
  isChatRequestTimeoutError: () => false,
}));

vi.mock('../services/telemetryHub', () => ({
  joinTelemetrySession: vi.fn(),
  onProgress: vi.fn(() => () => {}),
  onHubConnectionStatus: (listener: (s: string) => void) => {
    listener('connected');
    return () => {};
  },
  onHubHeartbeat: () => () => {},
  getHubConnectionStatus: () => 'connected',
  getLastHubHeartbeatAt: () => null,
}));

vi.mock('../services/executionControlApi', () => ({
  cancelChatSession: vi.fn().mockResolvedValue('cancelled'),
}));

vi.mock('../auth/activeProvider', () => ({
  activeAuthMode: 'anonymous',
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
});

import { ChatPanel } from '../components/ChatPanel';

function renderPanel() {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <ChatPanel />
    </FluentProvider>,
  );
}

describe('ChatPanel — anonymous mode execution-path guardrail', () => {
  it('hides the execution-path selector for anonymous sessions', () => {
    renderPanel();
    expect(screen.queryByTestId('execution-path-select')).not.toBeInTheDocument();
  });

  it('never sends forceExecutionPath for anonymous sessions', async () => {
    sendMessageMock.mockResolvedValue({
      kind: 'complete',
      response: {
        reply: 'ok',
        sessionId: 'sess-anon',
        spans: [],
        totalDurationMs: 10,
      },
    });

    const user = userEvent.setup();
    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'hi');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    const [request] = sendMessageMock.mock.calls[0];
    expect(request).toMatchObject({ message: 'hi' });
    expect(request).not.toHaveProperty('forceExecutionPath');
  });
});
