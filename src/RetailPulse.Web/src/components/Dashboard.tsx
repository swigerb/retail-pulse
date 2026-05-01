import { useState, useEffect, useCallback } from 'react';
import { Button, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
import { TelemetryPanel } from './TelemetryPanel';
import { BrandLogo } from './BrandLogo';
import type { AgentSpan } from '../types';
import { connectTelemetryHub, disconnectTelemetryHub } from '../services/telemetryHub';

const DRAWER_WIDTH_PX = 420;
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
  const styles = useStyles();

  // SignalR connection lives at Dashboard level so spans persist across drawer open/close
  useEffect(() => {
    connectTelemetryHub(
      (span) => setLiveSpans(prev => {
        const next = [...prev, span];
        return next.length > MAX_RETAINED_SPANS
          ? next.slice(next.length - MAX_RETAINED_SPANS)
          : next;
      }),
      () => setConnected(true),
      () => setConnected(false),
    );
    return () => { disconnectTelemetryHub(); };
  }, []);

  const handleNewChat = () => {
    setChatKey(prev => prev + 1);
    setLiveSpans([]);
  };

  const handleClearSpans = useCallback(() => setLiveSpans([]), []);

  return (
    <div className={styles.dashboard}>
      <header className={styles.header}>
        <div className={styles.headerBrand}>
          <BrandLogo size={36} />
          <span className={styles.headerTagline}>Brand Intelligence Platform</span>
        </div>
        <div className={styles.headerActions}>
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
          <ChatPanel key={chatKey} />
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
              📡 Real-Time Telemetry
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>
            <TelemetryPanel
              connected={connected}
              liveSpans={liveSpans}
              onClear={handleClearSpans}
            />
          </DrawerBody>
        </Drawer>
      </main>
    </div>
  );
}
