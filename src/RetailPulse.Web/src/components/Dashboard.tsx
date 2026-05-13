import { useState, useEffect, useCallback } from 'react';
import { Button, Badge, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
import { TelemetryPanel } from './TelemetryPanel';
import { AgentRoutingPanel } from './AgentRoutingPanel';
import { MemoryPanel } from './MemoryPanel';
import { ApprovalHistory } from './ApprovalHistory';
import { PendingApprovals } from './PendingApprovals';
import { BrandLogo } from './BrandLogo';
import type { AgentSpan, RoutingInfo, TokenUsage, ApprovalRequest, ApprovalDecision } from '../types';
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
          <ChatPanel
            key={chatKey}
            onResponseReceived={handleResponseReceived}
            approvals={pendingApprovals}
            onApprovalResolved={handleApprovalResolved}
          />
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
            <AgentRoutingPanel routingHistory={routingHistory} />
            {approvalHistory.length > 0 && (
              <div style={{ marginTop: '16px' }}>
                <ApprovalHistory approvals={approvalHistory} />
              </div>
            )}
            <div style={{ marginTop: '16px' }}>
              <MemoryPanel />
            </div>
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
