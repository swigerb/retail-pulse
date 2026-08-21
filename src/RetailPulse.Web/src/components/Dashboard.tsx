import { useState, useEffect, useCallback } from 'react';
import { Button, Badge, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular, TargetArrow24Regular, Shield24Regular, Library24Regular, HeartPulse24Regular, ShieldCheckmark24Regular, CardUi24Regular, Eye24Regular, Building24Regular, Money24Regular, Star24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
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
import { GuardrailsDashboard, GuardrailsConfig } from './guardrails';
import { AdaptiveCardPanel } from './cards';
import { ObservabilityPanel } from './observability';
import { StoreHeatmap, StockoutAlert, StorePerformanceTable, StoreDetailDialog } from './stores';
import { MarginWaterfall, MarginDrivers } from './margin';
import { PortfolioScorecard, BrandScoreCard, ExplanationPanel } from './scorecard';
import type { AgentSpan, RoutingInfo, TokenUsage, ApprovalRequest, ApprovalDecision, Alert, SnoozeDuration, Trace, TraceSpan, StorePerformance, StockoutRisk, MarginWaterfallStep, MarginDriver, BrandScore, ExplanationData } from '../types';
import { connectTelemetryHub } from '../services/telemetryHub';
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
  },
  headerTagline: {
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    letterSpacing: '0.5px',
    textTransform: 'uppercase',
    '@media (max-width: 600px)': {
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
    '@media (max-width: 600px)': {
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
  const [activeView, setActiveView] = useState<'chat' | 'promo' | 'competitive' | 'knowledge' | 'council' | 'security' | 'cards' | 'observability' | 'stores' | 'financials' | 'portfolio'>('chat');
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

  // Demo data for Phase 4 views
  const demoStores: StorePerformance[] = [
    { storeId: 's1', storeName: 'Flagship Downtown', region: 'Northeast', revenue: 2450000, target: 2200000, performanceIndex: 111, issues: [], recommendations: ['Expand premium shelf space'] },
    { storeId: 's2', storeName: 'Mall Central', region: 'Northeast', revenue: 1800000, target: 2000000, performanceIndex: 90, issues: ['Low foot traffic weekdays'], recommendations: ['Increase weekday promotions'] },
    { storeId: 's3', storeName: 'Suburb Plaza', region: 'Southeast', revenue: 950000, target: 1400000, performanceIndex: 68, issues: ['Stockout on top SKUs', 'Staff turnover'], recommendations: ['Urgent restock needed', 'Retention program'] },
    { storeId: 's4', storeName: 'Harbor View', region: 'West Coast', revenue: 1650000, target: 1500000, performanceIndex: 110, issues: [], recommendations: ['Expand beverage section'] },
    { storeId: 's5', storeName: 'Tech District', region: 'West Coast', revenue: 1200000, target: 1300000, performanceIndex: 92, issues: ['Display compliance low'], recommendations: ['Audit display compliance'] },
    { storeId: 's6', storeName: 'Lakeside', region: 'Midwest', revenue: 780000, target: 1100000, performanceIndex: 71, issues: ['Competitor opened nearby'], recommendations: ['Price match key SKUs'] },
    { storeId: 's7', storeName: 'Desert Springs', region: 'Southwest', revenue: 1100000, target: 1250000, performanceIndex: 88, issues: ['High shrinkage rate'], recommendations: ['Loss prevention audit'] },
    { storeId: 's8', storeName: 'Mesa Grande', region: 'Southwest', revenue: 920000, target: 1050000, performanceIndex: 87, issues: [], recommendations: ['Increase local brand assortment'] },
    { storeId: 's9', storeName: 'Rainier Square', region: 'Pacific Northwest', revenue: 1380000, target: 1400000, performanceIndex: 99, issues: [], recommendations: ['Launch loyalty program pilot'] },
    { storeId: 's10', storeName: 'Emerald Market', region: 'Pacific Northwest', revenue: 1050000, target: 1200000, performanceIndex: 88, issues: ['Weekend staffing gaps'], recommendations: ['Adjust weekend scheduling'] },
  ];

  const demoStockouts: StockoutRisk[] = [
    { skuId: 'sku1', skuName: 'Premium Blend 12pk', brand: 'Apex Grill', currentVelocity: 45, daysRemaining: 2, recommendedReorder: 500, region: 'Northeast' },
    { skuId: 'sku2', skuName: 'Classic Lager 6pk', brand: 'Summit Brew', currentVelocity: 32, daysRemaining: 5, recommendedReorder: 300, region: 'Southeast' },
    { skuId: 'sku3', skuName: 'Light Seltzer Variety', brand: 'Wave Drinks', currentVelocity: 28, daysRemaining: 6, recommendedReorder: 250, region: 'West Coast' },
  ];

  const demoWaterfall: MarginWaterfallStep[] = [
    { label: 'Revenue', value: 12500000, isSubtotal: true },
    { label: 'COGS', value: -7200000 },
    { label: 'Gross Margin', value: 5300000, isSubtotal: true },
    { label: 'Marketing', value: -1800000 },
    { label: 'Distribution', value: -950000 },
    { label: 'Net Margin', value: 2550000, isSubtotal: true },
  ];

  const demoDrivers: MarginDriver[] = [
    { name: 'Premium Mix Shift', impact: 3.2, trend: 'improving', isRisk: false },
    { name: 'Raw Material Costs', impact: -2.1, trend: 'worsening', isRisk: true },
    { name: 'Distribution Efficiency', impact: 1.5, trend: 'stable', isRisk: false },
    { name: 'Promotional Depth', impact: -1.8, trend: 'worsening', isRisk: true },
    { name: 'Channel Mix', impact: 0.9, trend: 'improving', isRisk: false },
  ];

  const demoBrands: BrandScore[] = [
    { brandName: 'Apex Grill', healthScore: 82, trend: 'up', dimensions: { demand: 88, margin: 75, competitive: 80, supply: 85 }, topRisk: 'Competitor pricing pressure', topOpportunity: 'Expand to Midwest' },
    { brandName: 'Summit Brew', healthScore: 65, trend: 'down', dimensions: { demand: 70, margin: 55, competitive: 68, supply: 72 }, topRisk: 'Margin erosion from COGS', topOpportunity: 'New seasonal SKU launch' },
    { brandName: 'Wave Drinks', healthScore: 91, trend: 'up', dimensions: { demand: 95, margin: 88, competitive: 90, supply: 92 }, topRisk: 'Supply chain capacity', topOpportunity: 'Premium tier expansion' },
    { brandName: 'Coastal Foods', healthScore: 45, trend: 'down', dimensions: { demand: 40, margin: 48, competitive: 42, supply: 50 }, topRisk: 'Market share loss to new entrants', topOpportunity: 'Rebrand + relaunch' },
    { brandName: 'Peak Snacks', healthScore: 73, trend: 'stable', dimensions: { demand: 78, margin: 70, competitive: 72, supply: 74 }, topRisk: 'Flat growth in core region', topOpportunity: 'E-commerce expansion' },
    { brandName: 'Valley Organics', healthScore: 58, trend: 'up', dimensions: { demand: 62, margin: 52, competitive: 55, supply: 64 }, topRisk: 'Low brand awareness', topOpportunity: 'Health trend tailwind' },
  ];

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
          {capabilities.alternateViews && featureFlags.campaignPlanner && (
            <Button
              appearance={activeView === 'promo' ? 'primary' : 'subtle'}
              icon={<TargetArrow24Regular />}
              onClick={() => setActiveView(prev => prev === 'promo' ? 'chat' : 'promo')}
              style={activeView === 'promo' ? { backgroundColor: '#22c55e', borderColor: '#22c55e' } : undefined}
            >
              {activeView === 'promo' ? 'Back to Chat' : 'Campaign Planner'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.competitive && (
            <Button
              appearance={activeView === 'competitive' ? 'primary' : 'subtle'}
              icon={<Shield24Regular />}
              onClick={() => setActiveView(prev => prev === 'competitive' ? 'chat' : 'competitive')}
              style={activeView === 'competitive' ? { backgroundColor: '#ef4444', borderColor: '#ef4444' } : undefined}
            >
              {activeView === 'competitive' ? 'Back to Chat' : 'Competitive'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.knowledgeBase && (
            <Button
              appearance={activeView === 'knowledge' ? 'primary' : 'subtle'}
              icon={<Library24Regular />}
              onClick={() => setActiveView(prev => prev === 'knowledge' ? 'chat' : 'knowledge')}
              style={activeView === 'knowledge' ? { backgroundColor: '#06b6d4', borderColor: '#06b6d4' } : undefined}
            >
              {activeView === 'knowledge' ? 'Back to Chat' : 'Knowledge Base'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.healthCouncil && (
            <Button
              appearance={activeView === 'council' ? 'primary' : 'subtle'}
              icon={<HeartPulse24Regular />}
              onClick={() => setActiveView(prev => prev === 'council' ? 'chat' : 'council')}
              style={activeView === 'council' ? { backgroundColor: '#0f7b0f', borderColor: '#0f7b0f' } : undefined}
            >
              {activeView === 'council' ? 'Back to Chat' : 'Health Council'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.security && (
            <Button
              appearance={activeView === 'security' ? 'primary' : 'subtle'}
              icon={<ShieldCheckmark24Regular />}
              onClick={() => setActiveView(prev => prev === 'security' ? 'chat' : 'security')}
              style={activeView === 'security' ? { backgroundColor: '#f59e0b', borderColor: '#f59e0b' } : undefined}
            >
              {activeView === 'security' ? 'Back to Chat' : 'Security'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.cards && (
            <Button
              appearance={activeView === 'cards' ? 'primary' : 'subtle'}
              icon={<CardUi24Regular />}
              onClick={() => setActiveView(prev => prev === 'cards' ? 'chat' : 'cards')}
              style={activeView === 'cards' ? { backgroundColor: '#3b82f6', borderColor: '#3b82f6' } : undefined}
            >
              {activeView === 'cards' ? 'Back to Chat' : 'Cards'}
            </Button>
          )}
          {capabilities.observability && featureFlags.observability && (
            <Button
              appearance={activeView === 'observability' ? 'primary' : 'subtle'}
              icon={<Eye24Regular />}
              onClick={() => setActiveView(prev => prev === 'observability' ? 'chat' : 'observability')}
              style={activeView === 'observability' ? { backgroundColor: '#06b6d4', borderColor: '#06b6d4' } : undefined}
            >
              {activeView === 'observability' ? 'Back to Chat' : 'Observability'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.stores && (
            <Button
              appearance={activeView === 'stores' ? 'primary' : 'subtle'}
              icon={<Building24Regular />}
              onClick={() => setActiveView(prev => prev === 'stores' ? 'chat' : 'stores')}
              style={activeView === 'stores' ? { backgroundColor: '#22c55e', borderColor: '#22c55e' } : undefined}
            >
              {activeView === 'stores' ? 'Back to Chat' : 'Stores'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.financials && (
            <Button
              appearance={activeView === 'financials' ? 'primary' : 'subtle'}
              icon={<Money24Regular />}
              onClick={() => setActiveView(prev => prev === 'financials' ? 'chat' : 'financials')}
              style={activeView === 'financials' ? { backgroundColor: '#3b82f6', borderColor: '#3b82f6' } : undefined}
            >
              {activeView === 'financials' ? 'Back to Chat' : 'Financials'}
            </Button>
          )}
          {capabilities.alternateViews && featureFlags.portfolio && (
            <Button
              appearance={activeView === 'portfolio' ? 'primary' : 'subtle'}
              icon={<Star24Regular />}
              onClick={() => setActiveView(prev => prev === 'portfolio' ? 'chat' : 'portfolio')}
              style={activeView === 'portfolio' ? { backgroundColor: '#8b5cf6', borderColor: '#8b5cf6' } : undefined}
            >
              {activeView === 'portfolio' ? 'Back to Chat' : 'Portfolio'}
            </Button>
          )}
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
            />
          </div>
          {capabilities.alternateViews && activeView === 'promo' && featureFlags.campaignPlanner ? (
            <div style={{ overflow: 'auto', height: '100%' }}>
              <PromoTaskModule />
            </div>
          ) : activeView === 'competitive' && featureFlags.competitive ? (
            <CompetitiveDashboard />
          ) : activeView === 'knowledge' && featureFlags.knowledgeBase ? (
            <KnowledgeBasePanel />
          ) : activeView === 'council' && featureFlags.healthCouncil ? (
            <CouncilPanel />
          ) : activeView === 'security' && featureFlags.security ? (
            <div style={{ overflow: 'auto', height: '100%' }}>
              <GuardrailsDashboard />
              <GuardrailsConfig />
            </div>
          ) : activeView === 'cards' && featureFlags.cards ? (
            <div style={{ overflow: 'auto', height: '100%' }}>
              <AdaptiveCardPanel />
            </div>
          ) : capabilities.observability && activeView === 'observability' && featureFlags.observability ? (
            <div style={{ overflow: 'auto', height: '100%' }}>
              <ObservabilityPanel />
            </div>
          ) : activeView === 'stores' && featureFlags.stores ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '20px' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '16px', fontSize: '20px' }}>🏪 Store Operations</h2>
              <StoreHeatmap stores={demoStores} onStoreClick={(id) => setSelectedStore(demoStores.find(s => s.storeId === id) ?? null)} />
              <div style={{ marginTop: '16px' }}>
                <StorePerformanceTable stores={demoStores} onStoreClick={(id) => setSelectedStore(demoStores.find(s => s.storeId === id) ?? null)} />
              </div>
              <div style={{ marginTop: '16px' }}>
                <h3 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '8px', fontSize: '14px' }}>📦 Stockout Risks</h3>
                <StockoutAlert risks={demoStockouts} />
              </div>
              <StoreDetailDialog store={selectedStore} open={!!selectedStore} onClose={() => setSelectedStore(null)} />
            </div>
          ) : activeView === 'financials' && featureFlags.financials ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '24px' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '20px', fontSize: '20px' }}>💰 Financials</h2>
              <MarginWaterfall steps={demoWaterfall} title="Q1 2026 P&L Waterfall" />
              <div style={{ marginTop: '24px' }}>
                <h3 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '12px', fontSize: '16px' }}>📈 Margin Drivers</h3>
                <MarginDrivers drivers={demoDrivers} />
              </div>
            </div>
          ) : activeView === 'portfolio' && featureFlags.portfolio ? (
            <div style={{ overflow: 'auto', height: '100%', padding: '24px' }}>
              <h2 style={{ color: 'var(--color-text)', fontFamily: "'Inter', system-ui, sans-serif", marginBottom: '20px', fontSize: '20px' }}>⭐ Portfolio Scorecard</h2>
              {selectedBrand ? (
                <div>
                  <Button appearance="subtle" onClick={() => setSelectedBrand(null)} style={{ marginBottom: '16px' }}>← Back to all brands</Button>
                  <BrandScoreCard brand={selectedBrand} onWhyClick={handleWhyClick} />
                </div>
              ) : (
                <PortfolioScorecard
                  brands={demoBrands}
                  generationTimeMs={3200}
                  onBrandClick={(name) => {
                    const brand = demoBrands.find(b => b.brandName === name);
                    if (brand) setSelectedBrand(brand);
                  }}
                  onWhyClick={handleWhyClick}
                />
              )}
              <ExplanationPanel explanation={explanationData} open={explanationOpen} onClose={() => setExplanationOpen(false)} />
            </div>
          ) : null}
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
