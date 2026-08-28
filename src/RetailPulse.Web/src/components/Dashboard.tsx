import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import type { ReactElement } from 'react';
import { Button, Badge, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle, Menu, MenuTrigger, MenuList, MenuItem, MenuPopover, MenuButton, Spinner } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular, TargetArrow24Regular, Shield24Regular, Library24Regular, HeartPulse24Regular, ShieldCheckmark24Regular, CardUi24Regular, Eye24Regular, Building24Regular, Money24Regular, Star24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
import { fetchFinancials, fetchScorecardBatched, fetchStores, fetchStockoutRisks } from '../services/operationsApi';
import { TelemetryPanel } from './TelemetryPanel';
import { AgentRoutingPanel } from './AgentRoutingPanel';
import { MemoryPanel } from './MemoryPanel';
import { CollapsibleSection } from './CollapsibleSection';
import { ApprovalHistory } from './ApprovalHistory';
import { PendingApprovals } from './PendingApprovals';
import { BrandLogo } from './BrandLogo';
import { AlertFeed } from './alerts';
import { AlertHistory as AlertHistoryPanel } from './alerts';
import { TraceDashboard } from './traces';
import { PromoTaskModule } from './promo';
import { CompetitiveDashboard } from './competitive';
import { KnowledgeBasePanel } from './knowledge';
import { CouncilPanel } from './council';
import { PanelErrorBoundary } from './PanelErrorBoundary';
import { GuardrailsDashboard, GuardrailsConfig } from './guardrails';
import { AdaptiveCardPanel } from './cards';
import { ObservabilityPanel } from './observability';
import { StoreHeatmap, StockoutAlert, StorePerformanceTable, StoreDetailDialog } from './stores';
import { MarginWaterfall, MarginDrivers } from './margin';
import { PortfolioScorecard, BrandScoreCard, ExplanationPanel } from './scorecard';
import type { AgentSpan, RoutingInfo, TokenUsage, ApprovalRequest, ApprovalDecision, Alert, SnoozeDuration, Trace, TraceSpan, StorePerformance, StockoutRisk, MarginWaterfallStep, MarginDriver, BrandScore, ExplanationData } from '../types';
import { connectTelemetryHub, subscribeHubEvent } from '../services/telemetryHub';
import { usePlanController } from '../state/usePlanController';
import { PlanHistoryPanel } from './plan';
import { featureFlags } from '../config/featureFlags';
import { capabilities, activeAuthMode, getActiveProvider } from '../auth/activeProvider';
import { AnonymousSessionBanner } from '../auth/gates/AnonymousAuthGate';
import type { AnonymousSessionProvider } from '../auth/providers/anonymousProvider';
import { useActivePack } from '../hooks/useActivePack';
import type { PackTheme } from '../types/pack';

const DRAWER_WIDTH_PX = 560;
const DRAWER_BREAKPOINT_PX = 768;

const drawerStyle: React.CSSProperties = {
  width: `min(${DRAWER_WIDTH_PX}px, 100vw)`,
  backgroundColor: 'var(--color-bg-elevated)',
  borderLeft: '1px solid var(--brand-accent-border-faint)',
};

// Applied to the persistent ChatPanel host while an alternate dashboard view
// (Observability, Approvals overlay, etc.) is active. `display: none` keeps the
// single ChatPanel instance mounted — preserving its messages, charts, session
// id/history, scroll, and any pending request — while fully removing it from the
// tab order and the screen-reader accessibility tree. Navigation no longer
// unmounts chat, so returning restores the exact conversation with no refetch.
const HIDDEN_CHAT_STYLE: React.CSSProperties = { display: 'none' };

const useStyles = makeStyles({
  dashboard: {
    display: 'flex',
    flexDirection: 'column',
    height: '100vh',
    backgroundColor: 'var(--color-bg)',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '0 28px',
    height: '64px',
    backgroundColor: 'var(--color-bg-elevated)',
    borderBottom: '2px solid var(--brand-accent)',
    '@media (max-width: 600px)': {
      padding: '0 12px',
    },
  },
  headerBrand: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    // Never let the brand block wrap or squeeze the nav. Without this the tagline
    // broke onto three lines and the tenant pill overlapped the nav buttons.
    flexShrink: 0,
    minWidth: 0,
  },
  headerTagline: {
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    letterSpacing: '0.5px',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
    '@media (max-width: 1500px)': {
      display: 'none',
    },
  },
  headerTenant: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: '999px',
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
    fontSize: '12px',
    color: 'var(--brand-accent-light)',
    background: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
    whiteSpace: 'nowrap',
    '@media (max-width: 1250px)': {
      display: 'none',
    },
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    transition: 'margin-right 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
  },
  headerActionsOpen: {
    marginRight: `${DRAWER_WIDTH_PX}px`,
    [`@media (max-width: ${DRAWER_BREAKPOINT_PX}px)`]: {
      marginRight: '0',
    },
  },
  main: {
    display: 'flex',
    flex: '1',
    overflow: 'hidden',
    position: 'relative',
  },
  chatContainer: {
    flex: '1',
    minWidth: '0',
    transition: 'margin-right 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
  },
  chatContainerOpen: {
    marginRight: `${DRAWER_WIDTH_PX}px`,
    [`@media (max-width: ${DRAWER_BREAKPOINT_PX}px)`]: {
      marginRight: '0',
    },
  },
  chatHost: {
    height: '100%',
  },
});

const MAX_RETAINED_SPANS = 500;
const MAX_ALERTS = 100;
const MAX_TRACES = 50;

/**
 * Human-readable label per dashboard view. Used by the per-panel error boundary
 * so a contained failure names the panel that failed.
 */
const VIEW_LABELS: Readonly<Record<string, string>> = {
  chat: 'Chat',
  promo: 'Campaign Planner',
  competitive: 'Competitive',
  knowledge: 'Knowledge Base',
  council: 'Health Council',
  security: 'Security',
  cards: 'Cards',
  observability: 'Observability',
  stores: 'Store Operations',
  financials: 'Financials',
  portfolio: 'Portfolio',
};

/**
 * How many view buttons stay inline before the rest collapse into "More".
 * Four keeps the header readable at ~1280px while still advertising the
 * headline capabilities.
 */
const MAX_INLINE_NAV_ITEMS = 4;

/** The dashboard views the header can navigate between. */
type ActiveView =
  | 'chat' | 'promo' | 'competitive' | 'knowledge' | 'council' | 'security'
  | 'cards' | 'observability' | 'stores' | 'financials' | 'portfolio';

// CSS custom properties overridden from the active pack's theme block.
// We intentionally stop at the two primary brand tokens; App.css already
// derives every semantic accent shade (accent-soft, border, hover, ...)
// from the base accent color, so overriding the base is enough to swap
// tenant colors coherently without hand-rolling every derived shade.
const THEME_CUSTOM_PROPERTIES = ['--brand-primary', '--brand-accent', '--brand-font-family'] as const;

function applyPackTheme(theme: PackTheme | null): (() => void) | undefined {
  if (!theme || typeof document === 'undefined') return undefined;
  const root = document.documentElement;
  const previous: Array<[string, string]> = THEME_CUSTOM_PROPERTIES.map((prop) => [prop, root.style.getPropertyValue(prop)]);
  if (theme.primaryColor) root.style.setProperty('--brand-primary', theme.primaryColor);
  if (theme.accentColor) root.style.setProperty('--brand-accent', theme.accentColor);
  if (theme.fontFamily) root.style.setProperty('--brand-font-family', theme.fontFamily);
  return () => {
    for (const [prop, value] of previous) {
      if (value) root.style.setProperty(prop, value);
      else root.style.removeProperty(prop);
    }
  };
}

export function Dashboard() {
  const [telemetryOpen, setTelemetryOpen] = useState(false);
  const [chatKey, setChatKey] = useState(0);
  const [connected, setConnected] = useState(false);
  const [liveSpans, setLiveSpans] = useState<AgentSpan[]>([]);
  const [totalDurationMs, setTotalDurationMs] = useState<number | undefined>();
  const [totalTokenUsage, setTotalTokenUsage] = useState<TokenUsage | undefined>();
  const [routingHistory, setRoutingHistory] = useState<RoutingInfo[]>([]);
  const [memoryRefreshKey, setMemoryRefreshKey] = useState(0);
  const [pendingApprovals, setPendingApprovals] = useState<ApprovalRequest[]>([]);
  const [approvalHistory, setApprovalHistory] = useState<ApprovalRequest[]>([]);
  const [alerts, setAlerts] = useState<Alert[]>([]);
  const [traces, setTraces] = useState<Trace[]>([]);
  const [activeView, setActiveView] = useState<ActiveView>('chat');
  const [selectedBrand, setSelectedBrand] = useState<BrandScore | null>(null);
  const [selectedStore, setSelectedStore] = useState<StorePerformance | null>(null);
  const [explanationOpen, setExplanationOpen] = useState(false);
  const [explanationData, setExplanationData] = useState<ExplanationData | null>(null);
  const styles = useStyles();

  // Active content pack. `useActivePack` starts with the built-in prompt
  // categories so the welcome-state chip grid renders on the first paint,
  // then swaps in the pack-supplied categories, tenant, and theme once
  // the /api/pack + /api/pack/starting-tasks fan-out resolves.
  const activePack = useActivePack();
  const packTheme = activePack.pack?.tenant.theme ?? null;
  useEffect(() => applyPackTheme(packTheme), [
    packTheme?.primaryColor,
    packTheme?.accentColor,
    packTheme?.fontFamily,
  ]);

  // Plan controller (issue #96). Shared between ChatPanel (renders the plan
  // surface for a plan-first response) and the Plan History panel in the
  // telemetry drawer. Backed by the same SignalR connection the rest of the
  // dashboard uses, so plan/step spans continue to flow through the existing
  // telemetry panel — no parallel telemetry system.
  const planConnectionRef = useRef({
    connected: false,
    on: (event: string, handler: (payload: unknown) => void) =>
      subscribeHubEvent(event, handler),
  });
  planConnectionRef.current.connected = connected;
  const planController = usePlanController({ connection: planConnectionRef.current });

  // Refresh plan history whenever the telemetry drawer opens so the user
  // sees a current list without a manual refresh, and once at boot.
  useEffect(() => {
    if (!capabilities.approvals) return;
    void planController.reloadHistory();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    if (telemetryOpen) void planController.reloadHistory();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [telemetryOpen]);

  // Demo data for Phase 4 views
  // Live operational data. These panels previously rendered hardcoded arrays
  // declared right here, so Financials, Store Operations and Portfolio showed
  // fabricated numbers that reconciled with nothing the system knew. They now
  // load from the API on first view of their panel.
  const [stores, setStores] = useState<StorePerformance[]>([]);
  const [stockouts, setStockouts] = useState<StockoutRisk[]>([]);
  const [waterfall, setWaterfall] = useState<MarginWaterfallStep[]>([]);
  const [drivers, setDrivers] = useState<MarginDriver[]>([]);
  const [financialsPeriod, setFinancialsPeriod] = useState<string>('');
  const [brands, setBrands] = useState<BrandScore[]>([]);
  const [brandsDurationMs, setBrandsDurationMs] = useState(0);
  const [brandsLoading, setBrandsLoading] = useState(false);
  const [brandsError, setBrandsError] = useState<string | null>(null);

  // The scorecard fans out real agent assessments per brand, so it is genuinely slow.
  // Load it on demand and show progress rather than blocking behind an empty grid.
  // Guarded with a ref, not state: putting the loading flag in the dependency array
  // made the effect re-run the moment it set the flag, whose cleanup then cancelled
  // the fetch it had just started. Batches came back 200 and were thrown away, and
  // the panel span forever.
  const scorecardRequested = useRef(false);

  useEffect(() => {
    if (activeView !== 'portfolio' || scorecardRequested.current) return;
    scorecardRequested.current = true;
    setBrandsLoading(true);
    void (async () => {
      const packBrands = activePack.pack?.tenant.brands?.map(b => b.name) ?? [];
      // Each brand fans out five specialist assessments, so the whole pack (11+
      // brands) would be 55 agent calls. Cap the panel at a portfolio-sized slice.
      const target = (packBrands.length > 0 ? packBrands : ['Apex Grill', 'Summit Vodka', 'FreshMart']).slice(0, 6);
      await fetchScorecardBatched(target, (scored, elapsedMs) => {
        // Render each batch as it lands rather than waiting for the whole portfolio.
        setBrands(scored);
        setBrandsDurationMs(elapsedMs);
      }).catch((e: unknown) => {
        setBrandsError(e instanceof Error ? e.message : 'Portfolio assessment failed.');
      });
      setBrandsLoading(false);
    })();
  }, [activeView, activePack.pack]);

  // Load operational data lazily, when its panel is first opened, so the chat path
  // does not pay for reads it will not render.
  useEffect(() => {
    if (activeView !== 'stores') return;
    let cancelled = false;
    void (async () => {
      const [s, r] = await Promise.all([fetchStores(), fetchStockoutRisks()]);
      if (cancelled) return;
      setStores(s);
      setStockouts(r);
    })();
    return () => { cancelled = true; };
  }, [activeView]);

  useEffect(() => {
    if (activeView !== 'financials') return;
    let cancelled = false;
    void (async () => {
      // Anchor on the pack's lead brand so the panel reports a real book of business
      // rather than an arbitrary one.
      const brand = activePack.pack?.tenant.brands?.[0]?.name ?? 'Apex Grill';
      const f = await fetchFinancials(brand);
      if (cancelled) return;
      setWaterfall(f.waterfall);
      setDrivers(f.drivers);
      setFinancialsPeriod(f.period ? `${f.period} P&L Waterfall` : 'P&L Waterfall');
    })();
    return () => { cancelled = true; };
  }, [activeView, activePack.pack]);

  const handleWhyClick = () => {
    setExplanationData({
      traceId: 'trace-demo-001',
      question: 'Why is this brand scored this way?',
      answer: 'The health score reflects a composite of demand, margin, competitive, and supply metrics weighted by recent performance trends.',
      steps: [
        { toolName: 'GetPortfolioDepletionStats', inputSummary: 'brand=all, period=Q1', outputSummary: '6 brands analyzed, 24 data points', reasoning: 'Queried depletion data to establish demand baseline across all regions.' },
        { toolName: 'GetMarginAnalysis', inputSummary: 'brand=all', outputSummary: 'Margin range: 22-41%', reasoning: 'Calculated gross margin for each brand to assess financial health dimension.' },
        { toolName: 'GetCompetitiveLandscape', inputSummary: 'category=all', outputSummary: '12 competitors tracked', reasoning: 'Assessed competitive positioning and recent market share movements.' },
      ],
      confidence: 87,
      dataSources: [
        { name: 'Q1 Depletion Report', url: '#' },
        { name: 'Margin Analysis Dashboard' },
        { name: 'Nielsen Competitive Data', url: '#' },
      ],
      generatedAt: new Date().toISOString(),
    });
    setExplanationOpen(true);
  };

  // SignalR connection lives at Dashboard level so spans persist across drawer open/close.
  // We intentionally do NOT disconnect on unmount — the connection is a module-level
  // singleton that survives React StrictMode double-mount and persists for the app lifetime.
  //
  // Gated by the active provider's capabilities: providers that forbid the real-time hubs
  // (Anonymous) never start SignalR. The hub access-token factory also returns '' for those
  // providers, so this is defense-in-depth on top of the backend's authoritative gate.
  useEffect(() => {
    if (!capabilities.realtimeHub) {
      return;
    }
    const conn = connectTelemetryHub(
      (span) => setLiveSpans(prev => {
        const next = [...prev, span];
        return next.length > MAX_RETAINED_SPANS
          ? next.slice(next.length - MAX_RETAINED_SPANS)
          : next;
      }),
      () => setConnected(true),
      () => setConnected(false),
    );

    // Listen for approval events on SignalR
    conn.off('approval_requested');
    conn.on('approval_requested', (approval: ApprovalRequest) => {
      setPendingApprovals(prev => {
        if (prev.some(a => a.id === approval.id)) return prev;
        return [...prev, { ...approval, status: 'pending' }];
      });
    });

    conn.off('approval_resolved');
    conn.on('approval_resolved', (resolved: { id: string; status: ApprovalDecision; decidedBy?: string; decidedAt?: string }) => {
      setPendingApprovals(prev => prev.filter(a => a.id !== resolved.id));
      setApprovalHistory(prev => {
        const existing = prev.find(a => a.id === resolved.id);
        if (existing) {
          return prev.map(a => a.id === resolved.id ? { ...a, ...resolved } : a);
        }
        return prev;
      });
    });

    // Alert events (Sprint 1.5)
    conn.off('alert_fired');
    conn.on('alert_fired', (alert: Alert) => {
      setAlerts(prev => {
        if (prev.some(a => a.id === alert.id)) return prev;
        const next = [{ ...alert, status: alert.status || 'active' as const }, ...prev];
        return next.length > MAX_ALERTS ? next.slice(0, MAX_ALERTS) : next;
      });
    });

    // Trace events (Sprint 1.6)
    conn.off('trace_started');
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    conn.on('trace_started', (data: any) => {
      const traceId = data.traceId || data.id;
      if (!traceId) return;
      const incomingIntent = data.intent;
      const incomingAgent = data.agentName;
      const incomingModel = data.model;
      setTraces(prev => {
        const existing = prev.find(t => t.traceId === traceId);
        if (existing) {
          // Enrich the existing trace if this trace_started carries richer metadata
          // (the first event from CaptureSpan has nulls; the EmitTraceStarted call
          // that follows routing carries the real intent/agent/model).
          if (!incomingIntent && !incomingAgent && !incomingModel) return prev;
          return prev.map(t =>
            t.traceId !== traceId ? t : {
              ...t,
              intent: incomingIntent || t.intent,
              agentName: incomingAgent || t.agentName,
              model: incomingModel || t.model,
            }
          );
        }
        const trace: Trace = {
          traceId,
          intent: incomingIntent || 'Processing...',
          agentName: incomingAgent || 'Unknown',
          model: incomingModel,
          startTime: data.timestamp || data.startTime || new Date().toISOString(),
          status: 'in_progress',
          totalDurationMs: data.totalDurationMs || 0,
          totalTokens: data.totalTokens || 0,
          totalCostUsd: data.totalCostUsd || 0,
          spans: data.spans || [],
        };
        const next = [trace, ...prev];
        return next.length > MAX_TRACES ? next.slice(0, MAX_TRACES) : next;
      });
    });

    conn.off('span_completed');
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    conn.on('span_completed', (data: any) => {
      // Support both nested shape { traceId, span: {...} } and flat shape { TraceId, SpanId, ... }
      let traceId: string;
      let span: TraceSpan;

      if (data?.span?.id) {
        // New backend shape: nested span object
        traceId = data.traceId;
        span = data.span;
        // Backend serializes span tags as `tags`; TS type expects `attributes`.
        // Map so consumers reading attributes['llm.model'] work.
        const rawTags = (data.span as { tags?: Record<string, string>; attributes?: Record<string, string> }).tags
          ?? (data.span as { attributes?: Record<string, string> }).attributes;
        if (rawTags && !span.attributes) {
          span = { ...span, attributes: rawTags };
        }
      } else if (data?.TraceId || data?.traceId || data?.SpanId || data?.spanId) {
        // Current backend shape: flat fields (PascalCase or camelCase)
        traceId = data.TraceId || data.traceId;
        const flatTags = data.Tags || data.tags;
        span = {
          id: data.SpanId || data.spanId,
          name: data.OperationName || data.operationName || 'Unknown',
          type: (data.SpanType || data.spanType || 'agent') as TraceSpan['type'],
          durationMs: data.DurationMs || data.durationMs || 0,
          startTime: data.StartTime || data.startTime || new Date().toISOString(),
          inputTokens: data.InputTokens || data.inputTokens,
          outputTokens: data.OutputTokens || data.outputTokens,
          estimatedCostUsd: data.EstimatedCostUsd || data.estimatedCostUsd,
          parentId: data.ParentId || data.parentId,
          attributes: flatTags,
        };
      } else {
        return; // Unrecognized shape, skip
      }

      if (!traceId || !span.id) return;

      setTraces(prev => prev.map(t => {
        if (t.traceId !== traceId) return t;
        if (t.spans.some(s => s.id === span.id)) return t;
        const tokenCount = (span.inputTokens || 0) + (span.outputTokens || 0);
        return {
          ...t,
          spans: [...t.spans, span],
          totalDurationMs: t.totalDurationMs + (span.durationMs || 0),
          totalTokens: t.totalTokens + tokenCount,
          totalCostUsd: t.totalCostUsd + (span.estimatedCostUsd || 0),
        };
      }));
    });

    conn.off('trace_completed');
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    conn.on('trace_completed', (data: any) => {
      setTraces(prev => prev.map(t => {
        if (t.traceId !== data.traceId) return t;
        return {
          ...t,
          status: 'completed' as const,
          totalDurationMs: data.totalDurationMs || t.totalDurationMs,
          totalTokens: data.totalTokens || t.totalTokens,
          totalCostUsd: data.totalCostUsd || t.totalCostUsd,
          intent: data.intent || (t.intent === 'Processing...' ? 'Completed' : t.intent),
          agentName: data.agentName || (t.agentName === 'Unknown' ? undefined : t.agentName) || t.agentName,
          model: data.model || t.model,
        };
      }));
    });
  }, []);

  const handleNewChat = () => {
    setChatKey(prev => prev + 1);
    setLiveSpans([]);
    setTotalDurationMs(undefined);
    setTotalTokenUsage(undefined);
    setRoutingHistory([]);
  };

  const handleClearSpans = useCallback(() => {
    setLiveSpans([]);
    setTotalDurationMs(undefined);
    setTotalTokenUsage(undefined);
    setRoutingHistory([]);
  }, []);

  const handleResponseReceived = useCallback((response: { totalDurationMs?: number; tokenUsage?: TokenUsage; routing?: RoutingInfo }) => {
    setMemoryRefreshKey(prev => prev + 1);
    setTotalDurationMs(prev => (prev ?? 0) + (response.totalDurationMs ?? 0));
    if (response.routing) {
      setRoutingHistory(prev => [...prev, response.routing!]);
    }
    if (response.tokenUsage) {
      setTotalTokenUsage(prev => {
        if (!prev) return response.tokenUsage;
        return {
          inputTokens: prev.inputTokens + response.tokenUsage!.inputTokens,
          outputTokens: prev.outputTokens + response.tokenUsage!.outputTokens,
          totalTokens: prev.totalTokens + response.tokenUsage!.totalTokens,
          estimatedCostUsd: (prev.estimatedCostUsd ?? 0) + (response.tokenUsage!.estimatedCostUsd ?? 0),
        };
      });
    }
  }, []);

  const handleApprovalResolved = useCallback((id: string, decision: ApprovalDecision) => {
    setPendingApprovals(prev => prev.filter(a => a.id !== id));
    setApprovalHistory(prev => {
      const resolved = prev.find(a => a.id === id);
      if (resolved) {
        return prev.map(a => a.id === id ? { ...a, status: decision, decidedAt: new Date().toISOString() } : a);
      }
      // If it was only in pending, move it to history
      return [...prev, ...pendingApprovals
        .filter(a => a.id === id)
        .map(a => ({ ...a, status: decision, decidedAt: new Date().toISOString() }))];
    });
  }, [pendingApprovals]);

  const handleAlertDismiss = useCallback((id: string) => {
    setAlerts(prev => prev.map(a => a.id === id ? { ...a, status: 'dismissed' as const } : a));
  }, []);

  const handleAlertSnooze = useCallback((id: string, duration: SnoozeDuration) => {
    const durationMap: Record<SnoozeDuration, number> = {
      '1h': 3_600_000, '4h': 14_400_000, '24h': 86_400_000, '1wk': 604_800_000,
    };
    const snoozedUntil = new Date(Date.now() + durationMap[duration]).toISOString();
    setAlerts(prev => prev.map(a => a.id === id ? { ...a, status: 'snoozed' as const, snoozedUntil } : a));
  }, []);

  const handleClearAllAlerts = useCallback(() => {
    setAlerts(prev => prev.map(a => a.status === 'active' ? { ...a, status: 'dismissed' as const } : a));
  }, []);

  // Header navigation, derived from the enabled capability + feature-flag set.
  // Building this as data (rather than ten hand-written buttons) is what lets the
  // header collapse gracefully instead of overflowing once every flag is on.
  const navItems = useMemo(() => {
    const alt = capabilities.alternateViews;
    const all: Array<{ view: ActiveView; label: string; icon: ReactElement; color: string; enabled: boolean }> = [
      { view: 'competitive', label: 'Competitive', icon: <Shield24Regular />, color: '#ef4444', enabled: alt && featureFlags.competitive },
      { view: 'council', label: 'Health Council', icon: <HeartPulse24Regular />, color: '#0f7b0f', enabled: alt && featureFlags.healthCouncil },
      { view: 'knowledge', label: 'Knowledge Base', icon: <Library24Regular />, color: '#06b6d4', enabled: alt && featureFlags.knowledgeBase },
      { view: 'observability', label: 'Observability', icon: <Eye24Regular />, color: '#06b6d4', enabled: capabilities.observability && featureFlags.observability },
      { view: 'promo', label: 'Campaign Planner', icon: <TargetArrow24Regular />, color: '#22c55e', enabled: alt && featureFlags.campaignPlanner },
      { view: 'security', label: 'Security', icon: <ShieldCheckmark24Regular />, color: '#f59e0b', enabled: alt && featureFlags.security },
      { view: 'cards', label: 'Cards', icon: <CardUi24Regular />, color: '#3b82f6', enabled: alt && featureFlags.cards },
      { view: 'stores', label: 'Stores', icon: <Building24Regular />, color: '#22c55e', enabled: alt && featureFlags.stores },
      { view: 'financials', label: 'Financials', icon: <Money24Regular />, color: '#3b82f6', enabled: alt && featureFlags.financials },
      { view: 'portfolio', label: 'Portfolio', icon: <Star24Regular />, color: '#8b5cf6', enabled: alt && featureFlags.portfolio },
    ];
    return all.filter(i => i.enabled);
  }, [capabilities.alternateViews, capabilities.observability]);

  // Promote the active view out of the overflow so the operator can always see
  // which panel they are on — and click the same button to get back to chat.
  const { primaryNavItems, overflowNavItems } = useMemo(() => {
    const inline = navItems.slice(0, MAX_INLINE_NAV_ITEMS);
    const rest = navItems.slice(MAX_INLINE_NAV_ITEMS);
    const activeInRest = rest.find(i => i.view === activeView);
    return activeInRest
      ? { primaryNavItems: [...inline, activeInRest], overflowNavItems: rest.filter(i => i.view !== activeView) }
      : { primaryNavItems: inline, overflowNavItems: rest };
  }, [navItems, activeView]);

  // The chat surface is a SINGLE persistently-mounted ChatPanel. It is visible only
  // when no alternate dashboard view is active; otherwise it is hidden (but kept
  // mounted) so its conversation state survives navigation. This boolean mirrors —
  // exactly — the alternate-view conditions rendered below, so chat shows whenever
  // none of them match (including the "feature flag off" fall-through cases).
  const chatVisible = !(
    (capabilities.alternateViews && activeView === 'promo' && featureFlags.campaignPlanner) ||
    (activeView === 'competitive' && featureFlags.competitive) ||
    (activeView === 'knowledge' && featureFlags.knowledgeBase) ||
    (activeView === 'council' && featureFlags.healthCouncil) ||
    (activeView === 'security' && featureFlags.security) ||
    (activeView === 'cards' && featureFlags.cards) ||
    (capabilities.observability && activeView === 'observability' && featureFlags.observability) ||
    (activeView === 'stores' && featureFlags.stores) ||
    (activeView === 'financials' && featureFlags.financials) ||
    (activeView === 'portfolio' && featureFlags.portfolio)
  );

  return (
    <div className={styles.dashboard}>
      {activeAuthMode === 'anonymous' && (
        <AnonymousSessionBanner
          provider={getActiveProvider() as unknown as AnonymousSessionProvider}
        />
      )}
      <header className={styles.header}>
        <div className={styles.headerBrand}>
          <BrandLogo size={36} />
          <span className={styles.headerTagline}>Brand Intelligence Platform</span>
          {activePack.pack && (
            <span
              className={styles.headerTenant}
              data-testid="pack-tenant-label"
              title={activePack.pack.tenant.description}
            >
              {activePack.pack.tenant.company}
              {activePack.pack.tenant.industry ? ` · ${activePack.pack.tenant.industry}` : ''}
            </span>
          )}
        </div>
        <div className={`${styles.headerActions} ${telemetryOpen ? styles.headerActionsOpen : ''}`}>
          {capabilities.approvals && (
            <PendingApprovals
              pendingApprovals={pendingApprovals}
              onClick={() => setTelemetryOpen(true)}
            />
          )}
          {/* View navigation.
              These were ten individually-rendered buttons, which overflowed and
              crushed the header once every feature flag was enabled. They are now
              data-driven: the first few stay visible for discoverability and the
              remainder collapse into a "More" menu, so the header fits regardless
              of how many views a deployment enables. The active view is always
              promoted out of the overflow so the operator can see where they are
              and click back. */}
          {capabilities.alternateViews || capabilities.observability ? (
            <>
              {primaryNavItems.map(item => (
                <Button
                  key={item.view}
                  appearance={activeView === item.view ? 'primary' : 'subtle'}
                  icon={item.icon}
                  onClick={() => setActiveView(prev => (prev === item.view ? 'chat' : item.view))}
                  style={activeView === item.view ? { backgroundColor: item.color, borderColor: item.color } : undefined}
                >
                  {activeView === item.view ? 'Back to Chat' : item.label}
                </Button>
              ))}
              {overflowNavItems.length > 0 && (
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <MenuButton appearance="subtle" data-testid="nav-more">More</MenuButton>
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      {overflowNavItems.map(item => (
                        <MenuItem
                          key={item.view}
                          icon={item.icon}
                          onClick={() => setActiveView(item.view)}
                        >
                          {item.label}
                        </MenuItem>
                      ))}
                    </MenuList>
                  </MenuPopover>
                </Menu>
              )}
            </>
          ) : null}
          <Button
            appearance="subtle"
            icon={<Add24Regular />}
            onClick={handleNewChat}
          >
            New Chat
          </Button>
          {capabilities.telemetryPanel && (
            <Button
              appearance={telemetryOpen ? 'primary' : 'subtle'}
              icon={telemetryOpen ? <Dismiss24Regular /> : <DataUsage24Regular />}
              onClick={() => setTelemetryOpen(prev => !prev)}
              aria-expanded={telemetryOpen}
              aria-controls="telemetry-drawer"
            >
              {telemetryOpen ? 'Close' : 'Real-Time Telemetry'}
            </Button>
          )}
        </div>
      </header>

      <main className={styles.main}>
        <div className={`${styles.chatContainer} ${telemetryOpen ? styles.chatContainerOpen : ''}`}>
          {/*
            Persistent chat surface. A single ChatPanel instance stays mounted for the
            lifetime of the Dashboard (or until New Chat remounts it via `chatKey`), so
            switching to Observability/Approvals/any alternate view no longer discards
            the conversation, session id, charts, scroll, or an in-flight request. When
            an alternate view is active the host is `display:none` + `inert` +
            `aria-hidden`, which removes it from the tab order and the screen-reader
            tree while keeping its React state alive.
          */}
          <div
            className={styles.chatHost}
            data-testid="chat-host"
            style={chatVisible ? undefined : HIDDEN_CHAT_STYLE}
            inert={!chatVisible}
            aria-hidden={chatVisible ? undefined : true}
          >
            <ChatPanel
              key={chatKey}
              onResponseReceived={handleResponseReceived}
              approvals={pendingApprovals}
              onApprovalResolved={handleApprovalResolved}
              promptCategories={activePack.categories}
              planController={planController}
              planConnected={connected}
              telemetryOpen={telemetryOpen}
            />
          </div>
          {/* Secondary views are wrapped in a per-panel boundary keyed by the
              active view: an uncaught render error is contained to that panel
              instead of replacing the entire dashboard via the app-level
              boundary. Keying on activeView resets the boundary when the
              operator navigates, so a failed panel does not stay broken. */}
          <PanelErrorBoundary key={activeView} name={VIEW_LABELS[activeView] ?? 'This view'}>
          {capabilities.alternateViews && activeView === 'promo' && featureFlags.campaignPlanner ? (
            <div style={{ overflow: 'auto', height: '100%', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <PromoTaskModule />
            </div>
          ) : activeView === 'competitive' && featureFlags.competitive ? (
            <CompetitiveDashboard />
          ) : activeView === 'knowledge' && featureFlags.knowledgeBase ? (
            <KnowledgeBasePanel />
          ) : activeView === 'council' && featureFlags.healthCouncil ? (
            <CouncilPanel />
          ) : activeView === 'security' && featureFlags.security ? (
            <div style={{ overflow: 'auto', height: '100%', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <GuardrailsDashboard />
              <GuardrailsConfig />
            </div>
          ) : activeView === 'cards' && featureFlags.cards ? (
            <div style={{ overflow: 'auto', height: '100%', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <AdaptiveCardPanel />
            </div>
          ) : capabilities.observability && activeView === 'observability' && featureFlags.observability ? (
            <div style={{ overflow: 'auto', height: '100%', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <ObservabilityPanel />
            </div>
          ) : activeView === 'stores' && featureFlags.stores ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '20px' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '16px', fontSize: '20px' }}>🏪 Store Operations</h2>
              <StoreHeatmap stores={stores} onStoreClick={(id) => setSelectedStore(stores.find(s => s.storeId === id) ?? null)} />
              <div style={{ marginTop: '16px' }}>
                <StorePerformanceTable stores={stores} onStoreClick={(id) => setSelectedStore(stores.find(s => s.storeId === id) ?? null)} />
              </div>
              <div style={{ marginTop: '16px' }}>
                <h3 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '8px', fontSize: '14px' }}>📦 Stockout Risks</h3>
                <StockoutAlert risks={stockouts} />
              </div>
              <StoreDetailDialog store={selectedStore} open={!!selectedStore} onClose={() => setSelectedStore(null)} />
            </div>
          ) : activeView === 'financials' && featureFlags.financials ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '24px', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '20px', fontSize: '20px' }}>💰 Financials</h2>
              <MarginWaterfall steps={waterfall} title={financialsPeriod} />
              <div style={{ marginTop: '24px' }}>
                <h3 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '12px', fontSize: '16px' }}>📈 Margin Drivers</h3>
                <MarginDrivers drivers={drivers} />
              </div>
            </div>
          ) : activeView === 'portfolio' && featureFlags.portfolio ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '24px', width: '100%', minWidth: 0, boxSizing: 'border-box' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '20px', fontSize: '20px' }}>⭐ Portfolio Scorecard</h2>
              {selectedBrand ? (
                <div>
                  <Button appearance="subtle" onClick={() => setSelectedBrand(null)} style={{ marginBottom: '16px' }}>← Back to all brands</Button>
                  <BrandScoreCard brand={selectedBrand} onWhyClick={handleWhyClick} />
                </div>
              ) : brandsError && brands.length === 0 ? (
                <div style={{ padding: '32px', color: 'var(--color-danger, #ef4444)' }}>{brandsError}</div>
              ) : brandsLoading && brands.length === 0 ? (
                /* Each brand fans out five specialist assessments, so this genuinely
                   takes a while. Say so, rather than showing an empty grid that reads
                   as a broken panel. */
                <div style={{ display: 'flex', alignItems: 'center', gap: '12px', padding: '32px', color: 'var(--color-text-secondary)' }}>
                  <Spinner size="small" />
                  <span>Assessing the portfolio — each brand is scored across five specialist dimensions.</span>
                </div>
              ) : (
                <>
                  {brandsLoading && (
                    <div style={{ display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '12px', color: 'var(--color-text-secondary)', fontSize: '13px' }}>
                      <Spinner size="tiny" />
                      <span>Scoring the rest of the portfolio…</span>
                    </div>
                  )}
                  <PortfolioScorecard
                    brands={brands}
                    generationTimeMs={brandsDurationMs}
                    onBrandClick={(name) => {
                      const brand = brands.find(b => b.brandName === name);
                      if (brand) setSelectedBrand(brand);
                    }}
                    onWhyClick={handleWhyClick}
                  />
                </>
              )}
              <ExplanationPanel explanation={explanationData} open={explanationOpen} onClose={() => setExplanationOpen(false)} />
            </div>
          ) : null}
          </PanelErrorBoundary>
        </div>

        {capabilities.telemetryPanel && (
        <Drawer
          id="telemetry-drawer"
          type="overlay"
          position="end"
          size="medium"
          open={telemetryOpen}
          modalType="non-modal"
          style={drawerStyle}
        >
          <DrawerHeader>
            <DrawerHeaderTitle
              action={
                <Button
                  appearance="subtle"
                  icon={<Dismiss24Regular />}
                  onClick={() => setTelemetryOpen(false)}
                  aria-label="Close telemetry panel"
                />
              }
            >
              📡 Real-Time Telemetry{' '}
              <Badge
                appearance="filled"
                color={connected ? 'success' : 'danger'}
                style={{ marginLeft: 8, verticalAlign: 'middle' }}
              >
                {connected ? '🟢 Live' : '🔴 Off'}
              </Badge>
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>
            {alerts.length > 0 && (
              <div style={{ marginBottom: '16px' }}>
                <AlertFeed
                  alerts={alerts}
                  onDismiss={handleAlertDismiss}
                  onSnooze={handleAlertSnooze}
                  onClearAll={handleClearAllAlerts}
                />
              </div>
            )}
            {alerts.some(a => a.status !== 'active') && (
              <div style={{ marginBottom: '16px' }}>
                <AlertHistoryPanel alerts={alerts} />
              </div>
            )}
            <CollapsibleSection title="Agent Routing">
              <AgentRoutingPanel routingHistory={routingHistory} />
            </CollapsibleSection>
            {capabilities.approvals && (
              <CollapsibleSection title="Plans">
                <PlanHistoryPanel
                  plans={planController.state.history}
                  loading={planController.state.historyLoading}
                  error={planController.state.historyError}
                  unavailable={planController.state.historyUnavailable}
                  activePlanId={planController.active?.planId ?? null}
                  onRefresh={() => { void planController.reloadHistory(); }}
                  onOpen={id => { void planController.openHistoryPlan(id); }}
                  onDelete={id => { void planController.removePlanFromHistory(id); }}
                />
              </CollapsibleSection>
            )}
            {approvalHistory.length > 0 && (
              <CollapsibleSection title="Approval History">
                <ApprovalHistory approvals={approvalHistory} />
              </CollapsibleSection>
            )}
            {capabilities.memory && (
              <CollapsibleSection title="Memory">
                <MemoryPanel refreshKey={memoryRefreshKey} />
              </CollapsibleSection>
            )}
            {traces.length > 0 && (
              <CollapsibleSection title="Trace Dashboard">
                <TraceDashboard traces={traces} />
              </CollapsibleSection>
            )}
            <CollapsibleSection title="Live Spans" defaultExpanded>
              <TelemetryPanel
                connected={connected}
                liveSpans={liveSpans}
                totalDurationMs={totalDurationMs}
                totalTokenUsage={totalTokenUsage}
                onClear={handleClearSpans}
              />
            </CollapsibleSection>
          </DrawerBody>
        </Drawer>
        )}
      </main>
    </div>
  );
}