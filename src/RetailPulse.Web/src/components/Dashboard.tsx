import { useState, useEffect, useCallback } from 'react';
import { Button, Badge, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular, TargetArrow24Regular, Shield24Regular, Library24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
import { TelemetryPanel } from './TelemetryPanel';
import { AgentRoutingPanel } from './AgentRoutingPanel';
import { MemoryPanel } from './MemoryPanel';
import { ApprovalHistory } from './ApprovalHistory';
import { PendingApprovals } from './PendingApprovals';
import { BrandLogo } from './BrandLogo';
import { AlertFeed } from './alerts';
import { AlertHistory as AlertHistoryPanel } from './alerts';
import { TraceDashboard } from './traces';
import { PromoTaskModule } from './promo';
import { CompetitiveDashboard } from './competitive';
import { KnowledgeBasePanel } from './knowledge';
import type { AgentSpan, RoutingInfo, TokenUsage, ApprovalRequest, ApprovalDecision, Alert, SnoozeDuration, Trace, TraceSpan } from '../types';
import { connectTelemetryHub } from '../services/telemetryHub';

const DRAWER_WIDTH_PX = 560;
const DRAWER_BREAKPOINT_PX = 768;

const drawerStyle: React.CSSProperties = {
  width: `min(${DRAWER_WIDTH_PX}px, 100vw)`,
  backgroundColor: 'var(--color-bg-elevated)',
  borderLeft: '1px solid var(--brand-accent-border-faint)',
};

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
});

const MAX_RETAINED_SPANS = 500;
const MAX_ALERTS = 100;
const MAX_TRACES = 50;

export function Dashboard() {
  const [telemetryOpen, setTelemetryOpen] = useState(false);
  const [chatKey, setChatKey] = useState(0);
  const [connected, setConnected] = useState(false);
  const [liveSpans, setLiveSpans] = useState<AgentSpan[]>([]);
  const [totalDurationMs, setTotalDurationMs] = useState<number | undefined>();
  const [totalTokenUsage, setTotalTokenUsage] = useState<TokenUsage | undefined>();
  const [routingHistory, setRoutingHistory] = useState<RoutingInfo[]>([]);
  const [pendingApprovals, setPendingApprovals] = useState<ApprovalRequest[]>([]);
  const [approvalHistory, setApprovalHistory] = useState<ApprovalRequest[]>([]);
  const [alerts, setAlerts] = useState<Alert[]>([]);
  const [traces, setTraces] = useState<Trace[]>([]);
  const [activeView, setActiveView] = useState<'chat' | 'promo' | 'competitive' | 'knowledge'>('chat');
  const styles = useStyles();

  // SignalR connection lives at Dashboard level so spans persist across drawer open/close.
  // We intentionally do NOT disconnect on unmount — the connection is a module-level
  // singleton that survives React StrictMode double-mount and persists for the app lifetime.
  useEffect(() => {
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
    conn.on('trace_started', (trace: Trace) => {
      setTraces(prev => {
        if (prev.some(t => t.traceId === trace.traceId)) return prev;
        const next = [{ ...trace, status: 'in_progress' as const, spans: trace.spans || [] }, ...prev];
        return next.length > MAX_TRACES ? next.slice(0, MAX_TRACES) : next;
      });
    });

    conn.off('span_completed');
    conn.on('span_completed', (data: { traceId: string; span: TraceSpan }) => {
      setTraces(prev => prev.map(t => {
        if (t.traceId !== data.traceId) return t;
        if (t.spans.some(s => s.id === data.span.id)) return t;
        return { ...t, spans: [...t.spans, data.span] };
      }));
    });

    conn.off('trace_completed');
    conn.on('trace_completed', (data: { traceId: string; totalDurationMs: number; totalTokens: number; totalCostUsd: number }) => {
      setTraces(prev => prev.map(t => {
        if (t.traceId !== data.traceId) return t;
        return { ...t, status: 'completed' as const, totalDurationMs: data.totalDurationMs, totalTokens: data.totalTokens, totalCostUsd: data.totalCostUsd };
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

  return (
    <div className={styles.dashboard}>
      <header className={styles.header}>
        <div className={styles.headerBrand}>
          <BrandLogo size={36} />
          <span className={styles.headerTagline}>Brand Intelligence Platform</span>
        </div>
        <div className={`${styles.headerActions} ${telemetryOpen ? styles.headerActionsOpen : ''}`}>
          <PendingApprovals
            pendingApprovals={pendingApprovals}
            onClick={() => setTelemetryOpen(true)}
          />
          <Button
            appearance={activeView === 'promo' ? 'primary' : 'subtle'}
            icon={<TargetArrow24Regular />}
            onClick={() => setActiveView(prev => prev === 'promo' ? 'chat' : 'promo')}
            style={activeView === 'promo' ? { backgroundColor: '#22c55e', borderColor: '#22c55e' } : undefined}
          >
            {activeView === 'promo' ? 'Back to Chat' : 'Campaign Planner'}
          </Button>
          <Button
            appearance={activeView === 'competitive' ? 'primary' : 'subtle'}
            icon={<Shield24Regular />}
            onClick={() => setActiveView(prev => prev === 'competitive' ? 'chat' : 'competitive')}
            style={activeView === 'competitive' ? { backgroundColor: '#ef4444', borderColor: '#ef4444' } : undefined}
          >
            {activeView === 'competitive' ? 'Back to Chat' : 'Competitive'}
          </Button>
          <Button
            appearance={activeView === 'knowledge' ? 'primary' : 'subtle'}
            icon={<Library24Regular />}
            onClick={() => setActiveView(prev => prev === 'knowledge' ? 'chat' : 'knowledge')}
            style={activeView === 'knowledge' ? { backgroundColor: '#06b6d4', borderColor: '#06b6d4' } : undefined}
          >
            {activeView === 'knowledge' ? 'Back to Chat' : 'Knowledge Base'}
          </Button>
          <Button
            appearance="subtle"
            icon={<Add24Regular />}
            onClick={handleNewChat}
          >
            New Chat
          </Button>
          <Button
            appearance={telemetryOpen ? 'primary' : 'subtle'}
            icon={telemetryOpen ? <Dismiss24Regular /> : <DataUsage24Regular />}
            onClick={() => setTelemetryOpen(prev => !prev)}
            aria-expanded={telemetryOpen}
            aria-controls="telemetry-drawer"
          >
            {telemetryOpen ? 'Close' : 'Telemetry'}
          </Button>
        </div>
      </header>

      <main className={styles.main}>
        <div className={`${styles.chatContainer} ${telemetryOpen ? styles.chatContainerOpen : ''}`}>
          {activeView === 'promo' ? (
            <div style={{ overflow: 'auto', height: '100%' }}>
              <PromoTaskModule />
            </div>
          ) : activeView === 'competitive' ? (
            <CompetitiveDashboard />
          ) : activeView === 'knowledge' ? (
            <KnowledgeBasePanel />
          ) : (
            <ChatPanel
              key={chatKey}
              onResponseReceived={handleResponseReceived}
              approvals={pendingApprovals}
              onApprovalResolved={handleApprovalResolved}
            />
          )}
        </div>

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
            <AgentRoutingPanel routingHistory={routingHistory} />
            {approvalHistory.length > 0 && (
              <div style={{ marginTop: '16px' }}>
                <ApprovalHistory approvals={approvalHistory} />
              </div>
            )}
            <div style={{ marginTop: '16px' }}>
              <MemoryPanel />
            </div>
            {traces.length > 0 && (
              <div style={{ marginTop: '16px' }}>
                <TraceDashboard traces={traces} />
              </div>
            )}
            <TelemetryPanel
              connected={connected}
              liveSpans={liveSpans}
              totalDurationMs={totalDurationMs}
              totalTokenUsage={totalTokenUsage}
              onClear={handleClearSpans}
            />
          </DrawerBody>
        </Drawer>
      </main>
    </div>
  );
}
