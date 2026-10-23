import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { ProviderCapabilities } from '../auth/session/types';
import { FULL_CAPABILITIES, ANONYMOUS_CAPABILITIES } from '../auth/session/types';
import type { PackTheme } from '../types/pack';

/**
 * Dashboard capability-gating integration test. The Dashboard reads a build-time `capabilities`
 * object to CENTRALLY hide/disable operator surfaces and to decide whether to start SignalR. These
 * tests drive that object per mode and assert the SignalR hub and the privileged surfaces are gated,
 * proving an anonymous build cannot start a hub or reach dashboards the backend would 403 anyway.
 */

const mockCaps: { value: ProviderCapabilities; mode: 'entra' | 'github' | 'anonymous' } = {
  value: FULL_CAPABILITIES,
  mode: 'entra',
};

const connectTelemetryHub = vi.fn(() => ({ on: vi.fn(), off: vi.fn() }));
const activePack: { theme: PackTheme | null } = { theme: null };
vi.mock('../hooks/useActivePack', () => ({
  useActivePack: () => ({
    pack: activePack.theme ? { tenant: { theme: activePack.theme } } : null,
    categories: [],
  }),
}));
vi.mock('../services/telemetryHub', () => ({
  connectTelemetryHub: () => connectTelemetryHub(),
  subscribeHubEvent: vi.fn(() => () => {}),
  joinTelemetrySession: vi.fn(() => Promise.resolve()),
  onProgress: vi.fn(() => () => {}),
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

// The plan history panel is stubbed like other drawer panels.
vi.mock('../components/plan', () => ({
  PlanHistoryPanel: () => null,
  PlanView: () => null,
  PlanStepRow: () => null,
  PlanReviewCard: () => null,
  PlanClarificationCard: () => null,
  PLAN_STATUS_META: {},
  PLAN_STEP_STATUS_META: {},
  formatElapsed: () => '0s',
  progressCounts: () => ({ total: 0, completed: 0, running: 0, failed: 0, pending: 0, percent: 0 }),
}));

vi.mock('../auth/activeProvider', () => ({
  get capabilities() {
    return mockCaps.value;
  },
  get activeAuthMode() {
    return mockCaps.mode;
  },
  getActiveProvider: () => ({ msUntilExpiry: () => 120000, endSession: vi.fn(), newSession: vi.fn() }),
}));

// All feature flags on, so any hidden alternate-view button is due to capability gating, not a flag.
vi.mock('../config/featureFlags', () => ({
  featureFlags: new Proxy({}, { get: () => true }),
}));

// Stub every heavy child so the test isolates Dashboard's own gating logic (not child behavior).
function stub(testid: string) {
  return () => <div data-testid={testid} />;
}
vi.mock('../components/ChatPanel', () => ({ ChatPanel: stub('chat-panel') }));
vi.mock('../components/TelemetryPanel', () => ({ TelemetryPanel: stub('telemetry-panel') }));
vi.mock('../components/AgentRoutingPanel', () => ({ AgentRoutingPanel: stub('agent-routing') }));
vi.mock('../components/MemoryPanel', () => ({ MemoryPanel: stub('memory-panel') }));
vi.mock('../components/CollapsibleSection', () => ({
  CollapsibleSection: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock('../components/ApprovalHistory', () => ({ ApprovalHistory: stub('approval-history') }));
vi.mock('../components/PendingApprovals', () => ({ PendingApprovals: stub('pending-approvals') }));
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
}));
vi.mock('../components/cards', () => ({ AdaptiveCardPanel: stub('cards') }));
vi.mock('../components/observability', () => ({
  ObservabilityPanel: () => <section data-testid="observability"><h1>Observability</h1></section>,
}));
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

function renderDashboard() {
  return render(
    <FluentProvider theme={teamsDarkTheme}>
      <Dashboard />
    </FluentProvider>,
  );
}

function setViewportWidth(width: number) {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
  window.dispatchEvent(new Event('resize'));
}

beforeEach(() => {
  connectTelemetryHub.mockClear();
  activePack.theme = null;
  setViewportWidth(1920);
});

describe('Dashboard pack theme lifecycle', () => {
  it.each([false, true])('restores prior theme properties on unmount (inline overrides: %s)', (hasOverrides) => {
    const root = document.documentElement;
    const properties = ['--brand-primary', '--brand-accent', '--brand-font-family'];
    const original = root.style.cssText;
    try {
      for (const prop of properties) root.style.removeProperty(prop);
      if (hasOverrides) {
        root.style.setProperty('--brand-primary', '#112233');
        root.style.setProperty('--brand-accent', '#445566');
        root.style.setProperty('--brand-font-family', 'serif');
      }
      const previous = properties.map(prop => root.style.getPropertyValue(prop));
      activePack.theme = {
        primaryColor: '#B3282D', accentColor: '#F4E3E3', fontFamily: 'Inter, Arial, sans-serif', logoPath: '',
      };
      const { rerender, unmount } = renderDashboard();
      expect(properties.map(prop => root.style.getPropertyValue(prop))).toEqual([
        '#B3282D', '#F4E3E3', 'Inter, Arial, sans-serif',
      ]);

      // A partial replacement must not retain the previous pack's accent or font.
      activePack.theme = { primaryColor: '#1F4E8C', accentColor: '', fontFamily: '', logoPath: '' };
      rerender(<FluentProvider theme={teamsDarkTheme}><Dashboard /></FluentProvider>);
      expect(properties.map(prop => root.style.getPropertyValue(prop))).toEqual([
        '#1F4E8C', previous[1], previous[2],
      ]);
      activePack.theme = null;
      rerender(<FluentProvider theme={teamsDarkTheme}><Dashboard /></FluentProvider>);
      expect(properties.map(prop => root.style.getPropertyValue(prop))).toEqual(previous);

      activePack.theme = { primaryColor: '#B3202A', accentColor: '#2B2B2B', fontFamily: '', logoPath: '' };
      rerender(<FluentProvider theme={teamsDarkTheme}><Dashboard /></FluentProvider>);
      unmount();
      expect(properties.map(prop => root.style.getPropertyValue(prop))).toEqual(previous);
    } finally {
      root.style.cssText = original;
    }
  });
});

afterEach(() => {
  vi.clearAllTimers();
});

describe('Dashboard — full capabilities (Entra/GitHub)', () => {
  beforeEach(() => {
    mockCaps.value = FULL_CAPABILITIES;
    mockCaps.mode = 'entra';
  });

  it('starts the SignalR telemetry hub', () => {
    renderDashboard();
    expect(connectTelemetryHub).toHaveBeenCalledTimes(1);
  });

  it('renders privileged surfaces and no anonymous banner', () => {
    renderDashboard();
    expect(screen.getByRole('button', { name: /Real-Time Telemetry/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Observability/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Competitive/i })).toBeInTheDocument();
    expect(screen.queryByTestId('anon-session-banner')).not.toBeInTheDocument();
  });

  it.each([957, 390])(
    'keeps every compact navigation action reachable at %ipx',
    async (width) => {
      setViewportWidth(width);
      const user = userEvent.setup();
      renderDashboard();

      await user.click(screen.getByRole('button', { name: 'Views' }));
      await user.click(screen.getByRole('menuitem', { name: 'Observability' }));

      expect(screen.getByRole('heading', { name: 'Observability' })).toBeInTheDocument();

      await user.click(screen.getByRole('button', { name: 'Actions' }));
      expect(screen.getByRole('menuitem', { name: 'New Chat' })).toBeInTheDocument();
      expect(screen.getByRole('menuitem', { name: 'Tour' })).toBeInTheDocument();
      expect(screen.getByRole('menuitem', { name: 'Demo Mode' })).toBeInTheDocument();
      expect(screen.getByRole('menuitem', { name: 'Real-Time Telemetry' })).toBeInTheDocument();
    },
  );
});

describe('Dashboard — anonymous capabilities (limited demo)', () => {
  beforeEach(() => {
    mockCaps.value = ANONYMOUS_CAPABILITIES;
    mockCaps.mode = 'anonymous';
  });

  it('NEVER starts the SignalR telemetry hub', () => {
    renderDashboard();
    expect(connectTelemetryHub).not.toHaveBeenCalled();
  });

  it('hides telemetry, observability, approvals, and all alternate-view surfaces', () => {
    renderDashboard();
    expect(screen.queryByRole('button', { name: /Real-Time Telemetry/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Observability/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Competitive/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Campaign Planner/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('pending-approvals')).not.toBeInTheDocument();
    expect(screen.queryByTestId('memory-panel')).not.toBeInTheDocument();
  });

  it('shows the limited-demo banner and still renders the chat surface', () => {
    renderDashboard();
    expect(screen.getByTestId('anon-session-banner')).toBeInTheDocument();
    expect(screen.getByTestId('chat-panel')).toBeInTheDocument();
    // "New Chat" is always available regardless of capabilities.
    expect(screen.getByRole('button', { name: /New Chat/i })).toBeInTheDocument();
  });
});
