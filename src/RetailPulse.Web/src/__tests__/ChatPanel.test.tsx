import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { GatewayCallEvidence, GatewayEvidence } from '../types';

// --- Mocks -----------------------------------------------------------------
// Stable mocks for the API + telemetry hub so ChatPanel can mount cleanly in
// jsdom without making real network calls or opening SignalR sockets.
const sendMessageMock = vi.fn();
const isErrorReplyMock = vi.fn((reply: string) => reply.startsWith('⏳'));

vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => isErrorReplyMock(reply),
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
  cancelPlan: vi.fn().mockResolvedValue('cancelled'),
}));

// activeAuthMode drives whether the execution-path selector is rendered.
// Default to `entra` (privileged mode) so the selector is present; the
// anonymous-mode test overrides the value via vi.doMock before importing.
vi.mock('../auth/activeProvider', () => ({
  activeAuthMode: 'entra',
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

function gatewayCall(overrides: Partial<GatewayCallEvidence> = {}): GatewayCallEvidence {
  return {
    gatewayRequestId: 'req-summary',
    backendAlias: 'retail-pulse-foundry-pool',
    route: 'primary',
    tokensConsumed: 10,
    tokensRemaining: 90,
    quota: 'allowed',
    cache: 'miss',
    safety: 'passed',
    gatewaySafety: {
      phase: 'output',
      decision: 'allow',
      outputChecked: true,
    },
    policyVersion: '2026-10-rp332-v1',
    status: 'observed',
    ...overrides,
  };
}

function gatewayEvidence(
  overrides: Partial<GatewayEvidence> = {},
  calls: GatewayCallEvidence[] = [gatewayCall()],
): GatewayEvidence {
  return {
    status: 'observed',
    observed: true,
    truncated: false,
    droppedCalls: 0,
    callCount: calls.length,
    totalTokensConsumed: 10,
    tokensRemaining: 90,
    calls,
    ...overrides,
  };
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
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'Sales were up 12% in Q1.',
      sessionId: 'sess-1',
      spans: [],
      totalDurationMs: 1234,
    } });

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
    // Auto is the default — the field must not appear on the wire so the
    // fast-path UX is byte-identical for the common case.
    expect(request).not.toHaveProperty('forceExecutionPath');

    // Assistant reply renders once the promise resolves.
    expect(await screen.findByText(/Sales were up 12% in Q1\./)).toBeInTheDocument();
  });

  it('collapses response telemetry and APIM evidence while keeping safety warnings visible', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'The requested summary is ready.',
      sessionId: 'sess-diagnostics',
      spans: [{
        name: 'GetHistoricalDemand',
        type: 'tool_call',
        detail: '{"region":"National"}',
        durationMs: 42,
        timestamp: '2026-10-22T17:00:00.000Z',
      }],
      totalDurationMs: 1200,
      tokenUsage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, estimatedCostUsd: 0.001 },
      gateway: {
        status: 'observed',
        observed: true,
        truncated: false,
        droppedCalls: 0,
        callCount: 1,
        totalTokensConsumed: 150,
        tokensRemaining: 850,
        calls: [{
          gatewayRequestId: 'req-warning',
          backendAlias: 'retail-pulse-foundry-pool',
          route: 'primary',
          tokensConsumed: 150,
          tokensRemaining: 850,
          quota: 'allowed',
          cache: 'unknown',
          safety: 'passed',
          gatewaySafety: {
            phase: 'input',
            decision: 'allow',
            promptAttack: false,
            category: null,
            severity: null,
            mode: 'demo',
            latencyMs: 12,
            outputChecked: false,
          },
          policyVersion: '2026-10-rp332-v1',
          status: 'observed',
        }],
      },
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Summarize demand');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('The requested summary is ready.')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Expand response information' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveTextContent('Safety warning: Allowed — output not checked');
    expect(toggle).toHaveAccessibleDescription(/Safety warning: Allowed — output not checked/);
    expect(screen.queryByText('GetHistoricalDemand')).not.toBeInTheDocument();
    expect(screen.queryByTestId('gateway-control-room')).not.toBeInTheDocument();

    await user.click(toggle);

    expect(screen.getByRole('button', { name: 'Collapse response information' }))
      .toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('GetHistoricalDemand')).toBeInTheDocument();
    expect(screen.getByTestId('gateway-control-room')).toBeInTheDocument();
    expect(screen.getByTestId('gateway-item-correlation')).toHaveTextContent('req-warning');
    expect(screen.getByTestId('gateway-item-policy')).toHaveTextContent('2026-10-rp332-v1');
  });

  it('does not summarize partial or unknown gateway evidence as fully observed', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'Partial gateway evidence was returned.',
      sessionId: 'sess-partial',
      spans: [],
      gateway: {
        status: 'observed',
        observed: true,
        truncated: false,
        droppedCalls: 0,
        callCount: 1,
        totalTokensConsumed: 10,
        tokensRemaining: null,
        calls: [{
          gatewayRequestId: null,
          backendAlias: null,
          route: 'unknown',
          tokensConsumed: 10,
          tokensRemaining: null,
          quota: 'allowed',
          cache: 'unknown',
          safety: 'passed',
          policyVersion: null,
          status: 'observed',
        }],
      },
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Check gateway state');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('Partial gateway evidence was returned.')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Expand response information' });
    expect(toggle).toHaveTextContent('Gateway details partial or unknown');
    expect(toggle).not.toHaveTextContent('Gateway observed');
    expect(toggle).toHaveAccessibleDescription(/Gateway details partial or unknown/);
  });

  it('renders response information after the complete prose and chart output', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'The chart follows this explanation.',
      sessionId: 'sess-chart-order',
      spans: [],
      charts: [{
        type: 'bar',
        title: 'Regional sales',
        data: [{ legend: 'Sales', values: [{ x: 'North', y: 42 }] }],
      }],
      gateway: gatewayEvidence(),
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Show regional sales');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('The chart follows this explanation.')).toBeInTheDocument();
    const chart = await screen.findByTestId('chart-renderer-mock');
    const diagnostics = screen.getByTestId('response-diagnostics');
    expect(chart.compareDocumentPosition(diagnostics) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
  });

  it('keeps safety warning and truncation signals together in the collapsed summary', async () => {
    const user = userEvent.setup();
    const unchecked = gatewayCall({
      gatewaySafety: { phase: 'input', decision: 'allow', outputChecked: false },
    });
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'Some gateway calls were omitted.',
      sessionId: 'sess-warning-truncated',
      spans: [],
      gateway: gatewayEvidence({ truncated: true, droppedCalls: 2, callCount: 3 }, [unchecked]),
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Check truncated evidence');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('Some gateway calls were omitted.')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Expand response information' });
    expect(toggle).toHaveTextContent('Safety warning: Allowed — output not checked');
    expect(toggle).toHaveTextContent('Gateway evidence truncated');
  });

  it('keeps throttling and unchecked-output safety signals together in the collapsed summary', async () => {
    const user = userEvent.setup();
    const throttledUnchecked = gatewayCall({
      quota: 'throttled',
      tokensRemaining: 0,
      gatewaySafety: { phase: 'input', decision: 'allow', outputChecked: false },
    });
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'The gateway rate limit was reached.',
      sessionId: 'sess-throttled-unchecked',
      spans: [],
      gateway: gatewayEvidence({ tokensRemaining: 0 }, [throttledUnchecked]),
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Check quota state');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('The gateway rate limit was reached.')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Expand response information' });
    expect(toggle).toHaveTextContent('Gateway throttled');
    expect(toggle).toHaveTextContent('Safety warning: Allowed — output not checked');
  });

  it('keeps partial gateway evidence and a safety warning together in the collapsed summary', async () => {
    const user = userEvent.setup();
    const partialUnchecked = gatewayCall({
      gatewayRequestId: null,
      backendAlias: null,
      route: 'unknown',
      cache: 'unknown',
      policyVersion: null,
      gatewaySafety: { phase: 'input', decision: 'allow', outputChecked: false },
    });
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'The gateway returned incomplete evidence.',
      sessionId: 'sess-partial-warning',
      spans: [],
      gateway: gatewayEvidence({}, [partialUnchecked]),
    } });

    renderPanel();
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'Check incomplete evidence');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    expect(await screen.findByText('The gateway returned incomplete evidence.')).toBeInTheDocument();
    const toggle = screen.getByRole('button', { name: 'Expand response information' });
    expect(toggle).toHaveTextContent('Safety warning: Allowed — output not checked');
    expect(toggle).toHaveTextContent('Gateway details partial or unknown');
  });

  it('exposes an Execution path selector defaulting to Auto', () => {
    renderPanel();

    const select = screen.getByTestId('execution-path-select') as HTMLSelectElement;
    expect(select).toBeInTheDocument();
    expect(select).toHaveAccessibleName('Execution path');
    expect(select.value).toBe('auto');
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['auto', 'fast', 'plan']);
  });

  it('includes forceExecutionPath when the user picks Fast', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'fast reply',
      sessionId: 'sess-fast',
      spans: [],
      totalDurationMs: 10,
    } });

    renderPanel();

    await user.selectOptions(screen.getByTestId('execution-path-select'), 'fast');
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'quick check');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    expect(sendMessageMock.mock.calls[0][0]).toMatchObject({
      message: 'quick check',
      forceExecutionPath: 'fast',
    });
  });

  it('includes forceExecutionPath when the user picks Plan', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'plan reply',
      sessionId: 'sess-plan',
      spans: [],
      totalDurationMs: 10,
    } });

    renderPanel();

    await user.selectOptions(screen.getByTestId('execution-path-select'), 'plan');
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'compare regions');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    expect(sendMessageMock.mock.calls[0][0]).toMatchObject({
      message: 'compare regions',
      forceExecutionPath: 'plan',
    });
  });

  it('drops back to Auto (omits the field) after switching Plan → Auto', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'ok',
      sessionId: 'sess-toggle',
      spans: [],
      totalDurationMs: 10,
    } });

    renderPanel();

    const select = screen.getByTestId('execution-path-select');
    await user.selectOptions(select, 'plan');
    await user.selectOptions(select, 'auto');
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'default now');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    const [request] = sendMessageMock.mock.calls[0];
    expect(request).toMatchObject({ message: 'default now' });
    expect(request).not.toHaveProperty('forceExecutionPath');
  });

  it('keeps the Prompt ideas library control available before and after a message is sent', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'Here is your answer.',
      sessionId: 'sess-lib',
      spans: [],
      totalDurationMs: 10,
    } });

    renderPanel();

    // Available on the empty welcome state, next to the composer.
    expect(screen.getByRole('button', { name: /Prompt ideas/i })).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'hello');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // Conversation has started (welcome chips are gone)…
    expect(await screen.findByText(/Here is your answer\./)).toBeInTheDocument();
    expect(screen.queryByText(/Welcome to Retail Pulse/i)).not.toBeInTheDocument();

    // …but the persistent prompt-library control remains next to the composer.
    expect(screen.getByRole('button', { name: /Prompt ideas/i })).toBeInTheDocument();
  });

  it('sends a prompt chosen from the persistent library after the conversation started', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'First reply.',
      sessionId: 'sess-lib2',
      spans: [],
      totalDurationMs: 10,
    } });

    renderPanel();

    // Start a conversation so the welcome state is gone.
    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'kick off');
    await user.click(screen.getByRole('button', { name: /Send message/i }));
    expect(await screen.findByText(/First reply\./)).toBeInTheDocument();

    sendMessageMock.mockClear();
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: 'Library reply.',
      sessionId: 'sess-lib2',
      spans: [],
      totalDurationMs: 10,
    } });

    // Open the library and pick a prompt. Fluent's trap-focus popover renders in a
    // portal and can nondeterministically apply aria-hidden to its surface under
    // jsdom, so anchor on the heading text (immune to aria-hidden) with a generous
    // timeout, then scope the prompt lookup to the surface including hidden nodes.
    // The full a11y contract is asserted in PromptLibrary.test.tsx (clean document).
    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    const prompt = 'Compare depletion trends across all regions for this quarter';
    const heading = await screen.findByText('Prompt library', { selector: 'h2' }, { timeout: 4000 });
    const surface = heading.closest('[role="dialog"]') as HTMLElement;
    await user.click(within(surface).getByRole('button', { name: prompt, hidden: true }));

    // The chosen prompt is dispatched through the same safe send path.
    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    expect(sendMessageMock.mock.calls[0][0]).toMatchObject({ message: prompt });
    expect(await screen.findByText(/Library reply\./)).toBeInTheDocument();
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
      kind: 'complete',
      response: {
        reply: 'Hello back!',
        sessionId: 'sess-x',
        spans: [],
        totalDurationMs: 50,
      },
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
    sendMessageMock.mockResolvedValue({ kind: 'complete', response: {
      reply: '⏳ The AI service is busy. Please wait a moment and try again.',
      sessionId: 'sess-err',
      spans: [],
      totalDurationMs: 100,
      routing: {
        selectedAgent: 'Demand Forecast Agent',
        confidence: 0.78,
        agentScores: {},
      },
    } });

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'forecast?');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // Error text renders…
    expect(await screen.findByText(/The AI service is busy/i)).toBeInTheDocument();

    // …but the misleading routing label must NOT be displayed.
    expect(screen.queryByText(/Demand Forecast Agent/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/78%/)).not.toBeInTheDocument();
  });

  it('fast-path response renders no plan chrome (issue #96 regression)', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      kind: 'complete',
      response: {
        reply: 'quick answer',
        sessionId: 'sess-fp',
        spans: [],
        totalDurationMs: 10,
        routing: {
          agentKey: 'demand-forecasting',
          agentName: 'Demand Agent',
          intent: 'demand/forecasting',
          confidence: 0.99,
          executionPath: 'fast',
        },
      },
    });

    renderPanel();

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'hi');
    await user.click(screen.getByRole('button', { name: /Send message/i }));
    expect(await screen.findByText(/quick answer/)).toBeInTheDocument();

    // Absolutely no plan surface, no plan status pill, and no plan step rows.
    expect(screen.queryByTestId('plan-view')).not.toBeInTheDocument();
    expect(screen.queryByTestId('plan-status-pill')).not.toBeInTheDocument();
    expect(screen.queryByTestId('plan-step-row')).not.toBeInTheDocument();
  });

  it('plan-first 200 response renders the plan view with the returned planId', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      kind: 'complete',
      response: {
        reply: 'aggregate reply',
        sessionId: 'sess-plan',
        spans: [],
        totalDurationMs: 200,
        routing: {
          agentKey: 'planner',
          agentName: 'Plan Orchestrator',
          intent: 'plan',
          confidence: 0.5,
          executionPath: 'plan',
        },
        planId: 'plan-42',
      },
    });

    const planController = {
      state: {
        active: {
          planId: 'plan-42',
          sessionId: 'sess-plan',
          request: 'multi-domain',
          status: 'completed' as const,
          steps: [],
          detectedIntents: [],
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          elapsedMs: 200,
          startedAt: Date.now(),
        },
        history: [],
        historyLoading: false,
      },
      active: null,
      startPlan: vi.fn().mockResolvedValue(undefined),
      hydrate: vi.fn().mockResolvedValue(undefined),
      approve: vi.fn().mockResolvedValue(undefined),
      reject: vi.fn().mockResolvedValue(undefined),
      edit: vi.fn().mockResolvedValue(undefined),
      clarify: vi.fn().mockResolvedValue(undefined),
      close: vi.fn(),
      reloadHistory: vi.fn().mockResolvedValue(undefined),
      removePlanFromHistory: vi.fn().mockResolvedValue(undefined),
      openHistoryPlan: vi.fn().mockResolvedValue(undefined),
      reportStepStatus: vi.fn(),
      applyFinalResponse: vi.fn(),
    } as unknown as import('../state/usePlanController').PlanController;
    // Point the "active" getter at state.active so PlanView renders.
    Object.defineProperty(planController, 'active', {
      get: () => planController.state.active,
    });

    renderPanel({ planController, planConnected: true });

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'compare');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(planController.startPlan).toHaveBeenCalledTimes(1));
    expect(planController.startPlan).toHaveBeenCalledWith(
      expect.objectContaining({ planId: 'plan-42', sessionId: 'sess-plan' }),
    );

    // The plan-first bubble shows the plan surface — not the plain reply.
    expect(await screen.findByTestId('plan-view')).toBeInTheDocument();
  });

  it('plan-first 200 with charts forwards them via applyFinalResponse (#141)', async () => {
    const user = userEvent.setup();
    const charts = [
      {
        type: 'bar' as const,
        title: 'Revenue by region',
        data: [{ legend: 'q1', values: [{ x: 'NE', y: 10 }] }],
      },
      {
        type: 'line' as const,
        title: 'Weekly trend',
        data: [{ legend: 'w', values: [{ x: 'w1', y: 5 }] }],
      },
    ];
    sendMessageMock.mockResolvedValue({
      kind: 'complete',
      response: {
        reply: 'aggregate reply with charts',
        sessionId: 'sess-plan',
        spans: [],
        totalDurationMs: 300,
        routing: {
          agentKey: 'planner',
          agentName: 'Plan Orchestrator',
          intent: 'plan',
          confidence: 0.5,
          executionPath: 'plan',
        },
        planId: 'plan-142',
        charts,
      },
    });

    const applyFinalResponseSpy = vi.fn();
    const planController = {
      state: {
        active: {
          planId: 'plan-142',
          sessionId: 'sess-plan',
          request: 'compare',
          status: 'completed' as const,
          steps: [],
          detectedIntents: [],
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          elapsedMs: 300,
          startedAt: Date.now(),
        },
        history: [],
        historyLoading: false,
      },
      active: null,
      startPlan: vi.fn().mockResolvedValue(undefined),
      hydrate: vi.fn().mockResolvedValue(undefined),
      approve: vi.fn().mockResolvedValue(undefined),
      reject: vi.fn().mockResolvedValue(undefined),
      edit: vi.fn().mockResolvedValue(undefined),
      clarify: vi.fn().mockResolvedValue(undefined),
      close: vi.fn(),
      reloadHistory: vi.fn().mockResolvedValue(undefined),
      removePlanFromHistory: vi.fn().mockResolvedValue(undefined),
      openHistoryPlan: vi.fn().mockResolvedValue(undefined),
      reportStepStatus: vi.fn(),
      applyFinalResponse: applyFinalResponseSpy,
    } as unknown as import('../state/usePlanController').PlanController;
    Object.defineProperty(planController, 'active', {
      get: () => planController.state.active,
    });

    renderPanel({ planController, planConnected: true });

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'compare');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(planController.startPlan).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(applyFinalResponseSpy).toHaveBeenCalledTimes(1));
    // The immediate path pushes reply + charts through the controller so
    // PlanView renders them exactly like the review-resume path.
    expect(applyFinalResponseSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        planId: 'plan-142',
        reply: 'aggregate reply with charts',
        charts,
      }),
    );
  });

  it('202 suspended response starts the plan and shows the review bubble', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      kind: 'suspended',
      suspended: {
        planId: 'plan-77',
        status: 'awaiting_review',
        reviewRequestId: 'req-1',
        round: 0,
        sessionId: 'sess-77',
        message: 'Plan is awaiting reviewer input.',
      },
    });

    const planController = {
      state: { active: null, history: [], historyLoading: false },
      active: null,
      startPlan: vi.fn().mockResolvedValue(undefined),
      hydrate: vi.fn(),
      approve: vi.fn(),
      reject: vi.fn(),
      edit: vi.fn(),
      clarify: vi.fn(),
      close: vi.fn(),
      reloadHistory: vi.fn().mockResolvedValue(undefined),
      removePlanFromHistory: vi.fn().mockResolvedValue(undefined),
      openHistoryPlan: vi.fn().mockResolvedValue(undefined),
      reportStepStatus: vi.fn(),
      applyFinalResponse: vi.fn(),
    } as unknown as import('../state/usePlanController').PlanController;

    renderPanel({ planController, planConnected: true });

    await user.type(screen.getByPlaceholderText(/Ask about retail performance/i), 'compare regions');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    await waitFor(() => expect(planController.startPlan).toHaveBeenCalledTimes(1));
    expect(planController.startPlan).toHaveBeenCalledWith(
      expect.objectContaining({ planId: 'plan-77', sessionId: 'sess-77' }),
    );
    expect(await screen.findByText(/awaiting reviewer input/i)).toBeInTheDocument();
    const diagnostics = screen.getByRole('button', { name: 'Expand response information' });
    expect(diagnostics).toHaveTextContent('Gateway metadata unavailable');
    await user.click(diagnostics);
    expect(screen.getByRole('group', { name: 'Gateway Control Room: Metadata unavailable' }))
      .toBeInTheDocument();
  });
});

describe('ChatPanel — issue #109 pack-sourced starting tasks', () => {
  it('renders the empty state when the active pack has no starting tasks', () => {
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <ChatPanel promptCategories={[]} />
      </FluentProvider>,
    );

    expect(screen.getByTestId('welcome-prompt-empty')).toBeInTheDocument();
    // Category chip row is suppressed so an "All" chip doesn't linger.
    expect(screen.queryByRole('button', { name: /🏪 All/i })).not.toBeInTheDocument();
  });

  it('renders the task display name on the button but submits the full prompt text', async () => {
    const user = userEvent.setup();
    sendMessageMock.mockResolvedValue({
      kind: 'complete',
      response: { reply: 'ok', sessionId: 'sess-name', spans: [], totalDurationMs: 10 },
    });

    render(
      <FluentProvider theme={teamsDarkTheme}>
        <ChatPanel
          promptCategories={[
            {
              id: 'pack',
              label: 'Pack',
              emoji: '🎁',
              tasks: [
                {
                  name: 'Depletion trend',
                  prompt: 'How is Meadowbowl Nutrition auto-ship depletion trending in the Sunbelt this quarter?',
                  capability: { kind: 'chart', chartType: 'line' },
                },
              ],
              prompts: [
                'How is Meadowbowl Nutrition auto-ship depletion trending in the Sunbelt this quarter?',
              ],
            },
          ]}
        />
      </FluentProvider>,
    );

    const btn = screen.getByRole('button', { name: 'Depletion trend' });
    expect(btn.textContent).toContain('Depletion trend');
    expect(btn.textContent).not.toContain('Meadowbowl');
    expect(btn.getAttribute('data-capability-kind')).toBe('chart');
    expect(btn.getAttribute('data-capability-chart-type')).toBe('line');

    await user.click(btn);

    await waitFor(() => expect(sendMessageMock).toHaveBeenCalledTimes(1));
    expect(sendMessageMock.mock.calls[0][0]).toMatchObject({
      message: 'How is Meadowbowl Nutrition auto-ship depletion trending in the Sunbelt this quarter?',
    });
  });

  it('re-renders welcome chips when the active pack projection swaps in a new categories list', () => {
    const initial = [
      {
        id: 'pack-a',
        label: 'Pack A',
        emoji: '🅰️',
        tasks: [{ name: 'A-task', prompt: 'a-prompt' }],
        prompts: ['a-prompt'],
      },
    ];
    const swapped = [
      {
        id: 'pack-b',
        label: 'Pack B',
        emoji: '🅱️',
        tasks: [{ name: 'B-task', prompt: 'b-prompt' }],
        prompts: ['b-prompt'],
      },
    ];

    const { rerender } = render(
      <FluentProvider theme={teamsDarkTheme}>
        <ChatPanel promptCategories={initial} />
      </FluentProvider>,
    );

    expect(screen.getByRole('button', { name: /🅰️ Pack A/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'A-task' })).toBeInTheDocument();

    rerender(
      <FluentProvider theme={teamsDarkTheme}>
        <ChatPanel promptCategories={swapped} />
      </FluentProvider>,
    );

    expect(screen.queryByRole('button', { name: /🅰️ Pack A/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'A-task' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /🅱️ Pack B/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'B-task' })).toBeInTheDocument();
  });
});
