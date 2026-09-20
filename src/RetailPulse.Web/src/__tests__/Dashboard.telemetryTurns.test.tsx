import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, act, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { AgentSpan } from '../types';
import type { ProviderCapabilities } from '../auth/session/types';
import { FULL_CAPABILITIES } from '../auth/session/types';

/**
 * Regression suite for issue #303 — Live Spans panel used to accumulate every
 * SignalR span into a single flat list, letting spans from prompt N-1 sit
 * inches from the answer for prompt N with no visual boundary. This suite
 * drives the REAL Dashboard + REAL ChatPanel + REAL TelemetryPanel through
 * two prompts with different tool calls and asserts:
 *   - Each turn renders as its own accessible region with the prompt in the header.
 *   - Spans from an earlier turn are NEVER attributed to the current turn.
 *   - The most recent turn is marked `aria-current="true"` so screen-reader users
 *     and viewers can both tell which spans belong to the answer beside them.
 *   - Clear Telemetry AND New Chat both reset spans + turns AND currentTurnId
 *     (so a stray hub frame after reset does not resurrect an old turn).
 */

// --- Service mocks ---------------------------------------------------------
const sendMessageMock = vi.fn();
const isErrorReplyMock = vi.fn((reply: string) => reply.startsWith('⏳'));
vi.mock('../services/api', () => ({
  sendMessage: (...args: unknown[]) => sendMessageMock(...args),
  isErrorReply: (reply: string) => isErrorReplyMock(reply),
}));

// Capture the SignalR span callback the Dashboard registers so tests can push
// spans directly and observe how the Live Spans panel groups them.
type SpanCallback = (span: AgentSpan) => void;
const spanCallbacks: SpanCallback[] = [];
const connectTelemetryHubMock = vi.fn<(...args: unknown[]) => {
  on: (evt: string, cb: (arg: unknown) => void) => void;
  off: (evt: string) => void;
}>((...args: unknown[]) => {
  spanCallbacks.push(args[0] as SpanCallback);
  return {
    on: () => {},
    off: () => {},
  };
});
const joinTelemetrySessionMock = vi.fn<(...args: unknown[]) => Promise<void>>(() => Promise.resolve());
vi.mock('../services/telemetryHub', () => ({
  connectTelemetryHub: (...args: unknown[]) => connectTelemetryHubMock(...(args as [SpanCallback])),
  joinTelemetrySession: (...args: unknown[]) => joinTelemetrySessionMock(...args),
  onProgress: vi.fn(() => () => {}),
  subscribeHubEvent: vi.fn(() => () => {}),
  onHubConnectionStatus: (listener: (s: string) => void) => {
    listener('connected');
    return () => {};
  },
  onHubHeartbeat: () => () => {},
  getHubConnectionStatus: () => 'connected',
  getLastHubHeartbeatAt: () => null,
}));

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

vi.mock('../components/ChartRenderer', () => ({
  default: () => <div data-testid="chart-renderer-mock" />,
}));

const mockCaps: { value: ProviderCapabilities; mode: 'entra' | 'github' | 'anonymous' } = {
  value: FULL_CAPABILITIES,
  mode: 'entra',
};
vi.mock('../auth/activeProvider', () => ({
  get capabilities() { return mockCaps.value; },
  get activeAuthMode() { return mockCaps.mode; },
  getActiveProvider: () => ({ msUntilExpiry: () => 120000, endSession: vi.fn(), newSession: vi.fn() }),
}));

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
    observability: false,
  },
}));

// Stub only the drawer panels we don't care about here. TelemetryPanel and
// TelemetryTurnGroup are LEFT REAL so the assertions exercise the actual
// grouping the user sees.
function stub(testid: string) {
  return () => <div data-testid={testid} />;
}
vi.mock('../components/AgentRoutingPanel', () => ({ AgentRoutingPanel: stub('agent-routing') }));
vi.mock('../components/MemoryPanel', () => ({ MemoryPanel: stub('memory-panel') }));
vi.mock('../components/CollapsibleSection', () => ({
  CollapsibleSection: ({ children, title }: { children: React.ReactNode; title: string }) => (
    <div data-testid={`collapsible-${title}`}>{children}</div>
  ),
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
vi.mock('../components/plan', () => ({
  PlanHistoryPanel: stub('plan-history'),
  PlanView: stub('plan-view'),
  PlanStepRow: () => null,
  PlanReviewCard: () => null,
  PlanClarificationCard: () => null,
  PLAN_STATUS_META: {},
  PLAN_STEP_STATUS_META: {},
  formatElapsed: () => '0s',
  progressCounts: () => ({ total: 0, completed: 0, running: 0, failed: 0, pending: 0, percent: 0 }),
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

const TURN1_PROMPT = 'Compare Harvest Table vs FreshMart sell-through rates by region';
const TURN2_PROMPT = 'How is Coastline Tacos doing in the Northeast compared to the Midwest?';

const turn1Response = {
  reply: 'Harvest Table leads FreshMart by 6% nationally.',
  sessionId: 'sess-1',
  spans: [],
  totalDurationMs: 800,
  charts: [],
};
const turn2Response = {
  reply: 'Coastline Tacos: Northeast +4%, Midwest -2%.',
  sessionId: 'sess-1',
  spans: [],
  totalDurationMs: 900,
  charts: [],
};

async function submitPrompt(user: ReturnType<typeof userEvent.setup>, prompt: string, replyText: string) {
  const input = screen.getByPlaceholderText(/Ask about retail performance/i);
  await user.clear(input);
  await user.type(input, prompt);
  await user.click(screen.getByRole('button', { name: /Send message/i }));
  await screen.findByText(replyText);
}

function pushSpan(partial: Partial<AgentSpan> & { name: string; detail: string }) {
  const span: AgentSpan = {
    name: partial.name,
    type: partial.type ?? 'tool_call',
    detail: partial.detail,
    durationMs: partial.durationMs ?? 12,
    timestamp: partial.timestamp ?? new Date().toISOString(),
  };
  act(() => {
    for (const cb of spanCallbacks) cb(span);
  });
}

beforeEach(() => {
  mockCaps.value = FULL_CAPABILITIES;
  mockCaps.mode = 'entra';
  sendMessageMock.mockReset();
  isErrorReplyMock.mockClear();
  isErrorReplyMock.mockImplementation((reply: string) => reply.startsWith('⏳'));
  joinTelemetrySessionMock.mockClear();
  connectTelemetryHubMock.mockClear();
  spanCallbacks.length = 0;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  vi.clearAllTimers();
});

// ---------------------------------------------------------------------------
describe('Dashboard — Live Spans panel groups spans by chat turn (issue #303)', { timeout: 15000 }, () => {
  it('shows two visibly separated turns after two prompts and never mixes their spans', async () => {
    const user = userEvent.setup();
    sendMessageMock
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn1Response })
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn2Response });

    renderDashboard();

    // The Dashboard registers a span callback as soon as it mounts.
    await waitFor(() => expect(spanCallbacks.length).toBeGreaterThan(0));

    // Open the telemetry drawer so the panel actually renders. The button
    // label toggles once the drawer opens.
    await user.click(screen.getByRole('button', { name: /Real-Time Telemetry/i }));

    // --- Turn 1: user submits the "Harvest Table vs FreshMart" prompt ------
    await submitPrompt(user, TURN1_PROMPT, turn1Response.reply);

    // Two tool-call spans arrive on the hub for turn 1 — these are the exact
    // spans that used to bleed into the next turn in the issue's demo.
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Harvest Table","region":"National"}' });
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"FreshMart","region":"National"}' });

    // Turn 1 group is visible with both spans, and it is the current turn.
    await waitFor(() => {
      const regions = screen.getAllByRole('region');
      expect(regions).toHaveLength(1);
    });
    let regions = screen.getAllByRole('region');
    expect(regions[0]).toHaveAttribute('aria-current', 'true');
    expect(
      within(regions[0]).getByLabelText(/^Prompt:.*Harvest Table.*FreshMart/i),
    ).toBeInTheDocument();
    expect(within(regions[0]).getByText(/"brand":"Harvest Table"/)).toBeInTheDocument();
    expect(within(regions[0]).getByText(/"brand":"FreshMart"/)).toBeInTheDocument();

    // --- Turn 2: user submits the "Coastline Tacos" prompt -----------------
    await submitPrompt(user, TURN2_PROMPT, turn2Response.reply);

    // Two spans arrive for turn 2 with the correct brand/region — this is
    // where the demo failure previously happened.
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Coastline Tacos","region":"Northeast"}' });
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Coastline Tacos","region":"Midwest"}' });

    await waitFor(() => {
      const groups = screen.getAllByRole('region');
      expect(groups).toHaveLength(2);
    });
    regions = screen.getAllByRole('region');
    const [turnOne, turnTwo] = regions;

    // The earlier turn is NOT marked current, and the newest turn IS.
    expect(turnOne).not.toHaveAttribute('aria-current');
    expect(turnOne).toHaveAttribute('data-turn-index', '1');
    expect(turnTwo).toHaveAttribute('aria-current', 'true');
    expect(turnTwo).toHaveAttribute('data-turn-index', '2');

    // Turn 1 still owns its Harvest Table / FreshMart spans and never sees
    // Coastline Tacos data. The prompt label is scoped to Turn 1 too.
    expect(within(turnOne).getByLabelText(/^Prompt:.*Harvest Table/i)).toBeInTheDocument();
    expect(within(turnOne).getByText(/"brand":"Harvest Table"/)).toBeInTheDocument();
    expect(within(turnOne).getByText(/"brand":"FreshMart"/)).toBeInTheDocument();
    expect(within(turnOne).queryByText(/Coastline Tacos/)).not.toBeInTheDocument();

    // Turn 2 shows ONLY the current-question spans. The exact misattribution
    // reported in issue #303 (Harvest Table / FreshMart spans appearing
    // alongside the Coastline Tacos answer) is now impossible.
    expect(
      within(turnTwo).getByLabelText(/^Prompt:.*Coastline Tacos/i),
    ).toBeInTheDocument();
    expect(within(turnTwo).getByText(/"region":"Northeast"/)).toBeInTheDocument();
    expect(within(turnTwo).getByText(/"region":"Midwest"/)).toBeInTheDocument();
    expect(within(turnTwo).queryByText(/Harvest Table/)).not.toBeInTheDocument();
    expect(within(turnTwo).queryByText(/FreshMart/)).not.toBeInTheDocument();

    // Header totals stayed cumulative (four spans, four tool calls) as
    // required by the issue — grouping did not shrink the session totals.
    const spansStat = screen.getByText('Spans').parentElement!;
    expect(within(spansStat).getByText('4')).toBeInTheDocument();
    const toolCallsStat = screen.getByText('Tool Calls').parentElement!;
    expect(within(toolCallsStat).getByText('4')).toBeInTheDocument();
  });

  it('Clear Telemetry drops both spans AND turns, and post-clear spans start a fresh turn', async () => {
    const user = userEvent.setup();
    sendMessageMock
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn1Response })
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn2Response });

    renderDashboard();
    await waitFor(() => expect(spanCallbacks.length).toBeGreaterThan(0));
    await user.click(screen.getByRole('button', { name: /Real-Time Telemetry/i }));

    await submitPrompt(user, TURN1_PROMPT, turn1Response.reply);
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Harvest Table","region":"National"}' });

    await waitFor(() => expect(screen.getAllByRole('region')).toHaveLength(1));

    // User presses Clear Telemetry.
    await user.click(screen.getByRole('button', { name: /Clear Telemetry/i }));

    // All turn groups and the Clear button disappear; the empty state returns.
    await waitFor(() => expect(screen.queryAllByRole('region')).toHaveLength(0));
    expect(screen.queryByRole('button', { name: /Clear Telemetry/i })).not.toBeInTheDocument();
    expect(screen.getByText(/Waiting for agent activity/i)).toBeInTheDocument();

    // A stray hub frame arriving after Clear must NOT resurrect the previous
    // turn — currentTurnId was reset to null, so an unattributed span lands
    // in the flat empty state (no region) until the next prompt.
    pushSpan({ name: 'StrayFrame', detail: 'late arrival' });
    expect(screen.queryAllByRole('region')).toHaveLength(0);

    // A new prompt opens a fresh turn 1 (index restarts, not turn 2).
    await submitPrompt(user, TURN2_PROMPT, turn2Response.reply);
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Coastline Tacos","region":"Northeast"}' });

    await waitFor(() => expect(screen.getAllByRole('region')).toHaveLength(1));
    const [region] = screen.getAllByRole('region');
    expect(region).toHaveAttribute('data-turn-index', '1');
    expect(region).toHaveAttribute('aria-current', 'true');
  });

  it('New Chat also resets turns and starts the next prompt back at turn 1', { timeout: 15000 }, async () => {
    const user = userEvent.setup();
    sendMessageMock
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn1Response })
      .mockResolvedValueOnce({ kind: 'complete' as const, response: turn2Response });

    renderDashboard();
    await waitFor(() => expect(spanCallbacks.length).toBeGreaterThan(0));
    await user.click(screen.getByRole('button', { name: /Real-Time Telemetry/i }));

    await submitPrompt(user, TURN1_PROMPT, turn1Response.reply);
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Harvest Table","region":"National"}' });
    await waitFor(() => expect(screen.getAllByRole('region')).toHaveLength(1));

    // New Chat wipes turns too — same reset semantics as Clear Telemetry.
    await user.click(screen.getByRole('button', { name: /New Chat/i }));
    await waitFor(() => expect(screen.queryAllByRole('region')).toHaveLength(0));

    await submitPrompt(user, TURN2_PROMPT, turn2Response.reply);
    pushSpan({ name: 'GetHistoricalDemand', detail: '{"brand":"Coastline Tacos","region":"Northeast"}' });

    await waitFor(() => expect(screen.getAllByRole('region')).toHaveLength(1));
    const [region] = screen.getAllByRole('region');
    expect(region).toHaveAttribute('data-turn-index', '1');
    expect(region).toHaveAttribute('aria-current', 'true');
    // And the panel now shows the Coastline Tacos prompt in the current-turn
    // header, not the earlier Harvest Table one that was wiped by New Chat.
    expect(within(region).getByLabelText(/^Prompt:.*Coastline Tacos/i)).toBeInTheDocument();
    expect(within(region).queryByText(/Harvest Table/)).not.toBeInTheDocument();
  });
});
