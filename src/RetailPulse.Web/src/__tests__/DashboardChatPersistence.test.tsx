import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, act, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { ProviderCapabilities } from '../auth/session/types';
import { FULL_CAPABILITIES } from '../auth/session/types';
import type { ApprovalRequest } from '../types';

/**
 * Regression suite for issue #46 — navigating to Observability (or opening the
 * Approvals overlay) must NOT clear the current conversation. Only "New Chat"
 * resets it.
 *
 * This exercises the REAL Dashboard + REAL ChatPanel together (the persistence
 * contract lives in how Dashboard hosts ChatPanel). The alternate destination
 * views and the telemetry-drawer panels are stubbed so the test isolates the
 * mount/visibility behaviour, not the destination screens.
 */

// --- Service mocks ---------------------------------------------------------
const sendMessageMock = vi.fn();
const isErrorReplyMock = vi.fn((reply: string) => reply.startsWith('⏳'));
vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => isErrorReplyMock(reply),
}));

// SignalR hub: Dashboard subscribes via connectTelemetryHub; ChatPanel joins a
// session group via joinTelemetrySession and subscribes to progress. We capture
// the hub event handlers so a test can push an `approval_requested` event, and
// we count joinTelemetrySession calls to PROVE the ChatPanel is not remounted on
// navigation (one join per real session).
const hubHandlers: Record<string, (arg: unknown) => void> = {};
const connectTelemetryHubMock = vi.fn<(...args: unknown[]) => {
  on: (evt: string, cb: (arg: unknown) => void) => void;
  off: (evt: string) => void;
}>(() => ({
  on: (evt: string, cb: (arg: unknown) => void) => { hubHandlers[evt] = cb; },
  off: (evt: string) => { delete hubHandlers[evt]; },
}));
const joinTelemetrySessionMock = vi.fn<(...args: unknown[]) => Promise<void>>(() => Promise.resolve());
vi.mock('../services/telemetryHub', () => ({
  connectTelemetryHub: (...args: unknown[]) => connectTelemetryHubMock(...args),
  joinTelemetrySession: (...args: unknown[]) => joinTelemetrySessionMock(...args),
  onProgress: vi.fn(() => () => {}),
  subscribeHubEvent: vi.fn(() => () => {}),
  // ChatPanel's ConnectionStatusIndicator (issue #92) calls useConnectionStatus,
  // which pulls these hub-status/heartbeat exports. Stub them so navigation
  // tests can render the real ChatPanel without needing a live SignalR stack.
  onHubConnectionStatus: (listener: (s: string) => void) => {
    listener('connected');
    return () => {};
  },
  onHubHeartbeat: () => () => {},
  getHubConnectionStatus: () => 'connected',
  getLastHubHeartbeatAt: () => null,
}));

// planApi is used by the plan controller wired into the Dashboard (issue #96).
// Return empty history / no plan detail so the plan surface stays inert unless a
// test explicitly opens it.
vi.mock('../services/planApi', () => ({
  fetchPlans: vi.fn(() => Promise.resolve([])),
  fetchPlanDetail: vi.fn(() => Promise.resolve(null)),
  fetchPlanReviews: vi.fn(() => Promise.resolve([])),
  decidePlanReview: vi.fn(() => Promise.resolve({})),
  answerPlanClarification: vi.fn(() => Promise.resolve()),
  deletePlan: vi.fn(() => Promise.resolve(false)),
  parseReviewProposal: vi.fn(() => null),
  parseClarificationPrompt: vi.fn(() => null),
}));

// ChartRenderer is lazy-loaded inside ChatPanel; mock so Suspense resolves
// synchronously and the presence of a chart is observable via a test id.
vi.mock('../components/ChartRenderer', () => ({
  default: () => <div data-testid="chart-renderer-mock" />,
}));

// Full (Entra) capabilities so Observability, Approvals, telemetry, and the hub
// are all enabled.
const mockCaps: { value: ProviderCapabilities; mode: 'entra' | 'github' | 'anonymous' } = {
  value: FULL_CAPABILITIES,
  mode: 'entra',
};
vi.mock('../auth/activeProvider', () => ({
  get capabilities() { return mockCaps.value; },
  get activeAuthMode() { return mockCaps.mode; },
  getActiveProvider: () => ({ msUntilExpiry: () => 120000, endSession: vi.fn(), newSession: vi.fn() }),
}));

// Only Observability is flagged on, so the sole alternate-view toggle button is
// Observability — its active label ("Back to Chat") is therefore unambiguous.
vi.mock('../config/featureFlags', () => ({
  featureFlags: {
    campaignPlanner: false,
    competitive: false,
    knowledgeBase: false,
    healthCouncil: false,
    security: false,
    cards: false,
    stores: false,
    financials: false,
    portfolio: false,
    observability: true,
  },
}));

// Stub heavy destination views + drawer-only panels. ChatPanel, PendingApprovals,
// and ApprovalCard are deliberately left REAL.
function stub(testid: string) {
  return () => <div data-testid={testid} />;
}
vi.mock('../components/TelemetryPanel', () => ({ TelemetryPanel: stub('telemetry-panel') }));
vi.mock('../components/AgentRoutingPanel', () => ({ AgentRoutingPanel: stub('agent-routing') }));
vi.mock('../components/MemoryPanel', () => ({ MemoryPanel: stub('memory-panel') }));
vi.mock('../components/CollapsibleSection', () => ({
  CollapsibleSection: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../components/ApprovalHistory', () => ({ ApprovalHistory: stub('approval-history') }));
vi.mock('../components/BrandLogo', () => ({ BrandLogo: stub('brand-logo') }));
vi.mock('../components/alerts', () => ({
  AlertFeed: stub('alert-feed'),
  AlertHistory: stub('alert-history'),
}));
vi.mock('../components/traces', () => ({ TraceDashboard: stub('trace-dashboard') }));
vi.mock('../components/promo', () => ({ PromoTaskModule: stub('promo') }));
vi.mock('../components/competitive', () => ({ CompetitiveDashboard: stub('competitive') }));
vi.mock('../components/knowledge', () => ({ KnowledgeBasePanel: stub('knowledge') }));
vi.mock('../components/council', () => ({ CouncilPanel: stub('council') }));
vi.mock('../components/guardrails', () => ({
  GuardrailsDashboard: stub('guardrails'),
  GuardrailsConfig: stub('guardrails-config'),
  BlockedRequestMessage: stub('blocked-request'),
}));
vi.mock('../components/cards', () => ({ AdaptiveCardPanel: stub('cards') }));
vi.mock('../components/observability', () => ({ ObservabilityPanel: stub('observability') }));
vi.mock('../components/stores', () => ({
  StoreHeatmap: stub('store-heatmap'),
  StockoutAlert: stub('stockout'),
  StorePerformanceTable: stub('store-table'),
  StoreDetailDialog: stub('store-dialog'),
}));
vi.mock('../components/margin', () => ({
  MarginWaterfall: stub('margin-waterfall'),
  MarginDrivers: stub('margin-drivers'),
}));
vi.mock('../components/scorecard', () => ({
  PortfolioScorecard: stub('portfolio'),
  BrandScoreCard: stub('brand-score'),
  ExplanationPanel: stub('explanation'),
}));

import { Dashboard } from '../components/Dashboard';

// --- Helpers ---------------------------------------------------------------
function renderDashboard() {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <Dashboard />
    </FluentProvider>,
  );
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => { resolve = r; });
  return { promise, resolve };
}

const CHART_RESPONSE = {
  reply: 'Q1 depletion rose 12% across all regions.',
  sessionId: 'sess-ignored',
  spans: [],
  totalDurationMs: 1234,
  charts: [
    {
      type: 'bar',
      title: 'Q1 Depletion by Region',
      data: [{ legend: 'Depletion', values: [{ label: 'NE', value: 12 }] }],
    },
  ],
};
const CHART_REPLY = { kind: 'complete' as const, response: CHART_RESPONSE };

async function seedConversation(user: ReturnType<typeof userEvent.setup>, prompt = 'How did Q1 depletion trend?') {
  const input = screen.getByPlaceholderText(/Ask about retail performance/i);
  await user.type(input, prompt);
  await user.click(screen.getByRole('button', { name: /Send message/i }));
  await screen.findByText(CHART_RESPONSE.reply);
  await waitFor(() => expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument());
}

const pendingApproval: ApprovalRequest = {
  id: 'ap-1',
  action: 'Send promotional email to 10,000 customers',
  reasoning: 'Campaign uplift is projected at 8%.',
  impact: 'External communication to customers.',
  urgency: 'high',
  agentId: 'agent-1',
  agentName: 'Campaign Agent',
  requestedAt: new Date().toISOString(),
  timeoutAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  status: 'pending',
};

beforeEach(() => {
  mockCaps.value = FULL_CAPABILITIES;
  mockCaps.mode = 'entra';
  sendMessageMock.mockReset();
  sendMessageMock.mockResolvedValue(CHART_REPLY);
  isErrorReplyMock.mockClear();
  isErrorReplyMock.mockImplementation((reply: string) => reply.startsWith('⏳'));
  joinTelemetrySessionMock.mockClear();
  connectTelemetryHubMock.mockClear();
  for (const k of Object.keys(hubHandlers)) delete hubHandlers[k];
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.clearAllTimers();
});

// ---------------------------------------------------------------------------
describe('Dashboard — chat persists across navigation (issue #46)', () => {
  it('keeps messages, chart, and session when switching to Observability and back', async () => {
    const user = userEvent.setup();
    renderDashboard();

    await seedConversation(user);
    expect(sendMessageMock).toHaveBeenCalledTimes(1);
    // Exactly one hub session join → a single, stable session was established.
    expect(joinTelemetrySessionMock).toHaveBeenCalledTimes(1);
    const sessionId = joinTelemetrySessionMock.mock.calls[0][0];

    // Navigate to Observability.
    await user.click(screen.getByRole('button', { name: 'Observability' }));
    expect(screen.getByTestId('observability')).toBeInTheDocument();

    // Return to Chat.
    await user.click(screen.getByRole('button', { name: /Back to Chat/i }));

    // Conversation is intact — same DOM nodes, never refetched or rejoined.
    expect(screen.getByText(CHART_RESPONSE.reply)).toBeInTheDocument();
    expect(screen.getByText(/How did Q1 depletion trend\?/i)).toBeInTheDocument();
    expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();
    expect(sendMessageMock).toHaveBeenCalledTimes(1);
    expect(joinTelemetrySessionMock).toHaveBeenCalledTimes(1);
    expect(joinTelemetrySessionMock.mock.calls[0][0]).toBe(sessionId);
  });

  it('surfaces a response that resolves while the user is viewing Observability', async () => {
    const user = userEvent.setup();
    const pending = deferred<typeof CHART_REPLY>();
    sendMessageMock.mockImplementationOnce(() => pending.promise);

    renderDashboard();

    const input = screen.getByPlaceholderText(/Ask about retail performance/i);
    await user.type(input, 'Give me the regional breakdown');
    await user.click(screen.getByRole('button', { name: /Send message/i }));

    // Navigate away while the request is still in flight.
    await user.click(screen.getByRole('button', { name: 'Observability' }));
    expect(screen.getByTestId('observability')).toBeInTheDocument();

    // The promise resolves while the chat host is hidden but still mounted.
    await act(async () => {
      pending.resolve(CHART_REPLY);
      await Promise.resolve();
    });

    // Returning shows the completed answer + chart — no replay, no lost result.
    await user.click(screen.getByRole('button', { name: /Back to Chat/i }));
    expect(await screen.findByText(CHART_RESPONSE.reply)).toBeInTheDocument();
    expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();
    expect(sendMessageMock).toHaveBeenCalledTimes(1);
  });

  it('opening and closing the Approvals overlay does not disturb the chat', async () => {
    const user = userEvent.setup();
    renderDashboard();

    await seedConversation(user);

    // A pending approval arrives over SignalR.
    await act(async () => {
      hubHandlers['approval_requested']?.(pendingApproval);
      await Promise.resolve();
    });
    expect(screen.getByTestId('pending-count-badge')).toHaveTextContent('1');
    // It renders inline in the chat stream too.
    expect(screen.getAllByTestId('approval-card').length).toBeGreaterThan(0);

    // Open the Approvals / telemetry overlay.
    await user.click(screen.getByTestId('pending-approvals-button'));

    // Chat is untouched: still visible, still holding its message + chart.
    const host = screen.getByTestId('chat-host');
    expect(host).not.toHaveAttribute('aria-hidden');
    expect(screen.getByText(CHART_RESPONSE.reply)).toBeInTheDocument();
    expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();

    // Close the overlay — returns to the same chat.
    await user.click(screen.getByRole('button', { name: /Close telemetry panel/i }));
    expect(screen.getByText(CHART_RESPONSE.reply)).toBeInTheDocument();
    expect(screen.getByTestId('chart-renderer-mock')).toBeInTheDocument();
    expect(sendMessageMock).toHaveBeenCalledTimes(1);
  });
});

describe('Dashboard — New Chat is the only reset (issue #46)', () => {
  it('clears messages, chart, and starts a fresh session', async () => {
    const user = userEvent.setup();
    renderDashboard();

    await seedConversation(user);
    expect(joinTelemetrySessionMock).toHaveBeenCalledTimes(1);
    const firstSession = joinTelemetrySessionMock.mock.calls[0][0];

    await user.click(screen.getByRole('button', { name: /New Chat/i }));

    // Fresh chat: welcome prompts return, prior message + chart are gone.
    expect(await screen.findByText(/Welcome to Retail Pulse/i)).toBeInTheDocument();
    expect(screen.queryByText(CHART_RESPONSE.reply)).not.toBeInTheDocument();
    expect(screen.queryByTestId('chart-renderer-mock')).not.toBeInTheDocument();

    // A brand-new session id was joined (ChatPanel remounted via chatKey).
    await waitFor(() => expect(joinTelemetrySessionMock).toHaveBeenCalledTimes(2));
    expect(joinTelemetrySessionMock.mock.calls[1][0]).not.toBe(firstSession);
  });
});

describe('Dashboard — accessibility of the persistent chat host (issue #46)', () => {
  it('marks the chat host inert + aria-hidden when an alternate view is active, and restores it on return', async () => {
    const user = userEvent.setup();
    renderDashboard();

    await seedConversation(user);
    const host = screen.getByTestId('chat-host');

    // Active chat: in the tab order + accessibility tree.
    expect(host).not.toHaveAttribute('aria-hidden');
    expect(host).not.toHaveAttribute('inert');

    // Switch to Observability: chat host removed from tab order + SR tree.
    await user.click(screen.getByRole('button', { name: 'Observability' }));
    expect(host).toHaveAttribute('aria-hidden', 'true');
    expect(host).toHaveAttribute('inert');
    expect(host).toHaveStyle({ display: 'none' });
    // The active view is visible and not inert.
    const active = screen.getByTestId('observability');
    expect(active).toBeVisible();
    expect(within(host).queryByPlaceholderText(/Ask about retail performance/i)).toBeInTheDocument();

    // Return: host restored to the accessibility tree.
    await user.click(screen.getByRole('button', { name: /Back to Chat/i }));
    expect(host).not.toHaveAttribute('aria-hidden');
    expect(host).not.toHaveAttribute('inert');
    expect(screen.getByPlaceholderText(/Ask about retail performance/i)).toBeVisible();
  });
});
