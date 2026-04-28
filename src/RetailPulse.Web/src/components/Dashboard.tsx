import { useState } from 'react';
import { Button, makeStyles, Drawer, DrawerBody, DrawerHeader, DrawerHeaderTitle } from '@fluentui/react-components';
import { Add24Regular, DataUsage24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { ChatPanel } from './ChatPanel';
import { TelemetryPanel } from './TelemetryPanel';
import { BrandLogo } from './BrandLogo';

const useStyles = makeStyles({
  dashboard: {
    display: 'flex',
    flexDirection: 'column',
    height: '100vh',
    backgroundColor: '#080808',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '0 28px',
    height: '64px',
    backgroundColor: '#0D0D0D',
    borderBottom: '2px solid var(--brand-accent)',
  },
  headerBrand: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
  },
  headerTagline: {
    fontFamily: "'Algerian', 'Playfair Display', serif",
    fontSize: '13px',
    color: '#A0A0A0',
    letterSpacing: '0.5px',
    textTransform: 'uppercase',
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
    marginRight: '420px',
  },
});

export function Dashboard() {
  const [telemetryOpen, setTelemetryOpen] = useState(false);
  const [chatKey, setChatKey] = useState(0);
  const styles = useStyles();

  const handleNewChat = () => {
    setChatKey(prev => prev + 1);
  };

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
          type="overlay"
          position="end"
          size="medium"
          open={telemetryOpen}
          onOpenChange={(_, { open }) => setTelemetryOpen(open)}
          style={{
            width: '420px',
            backgroundColor: '#0D0D0D',
            borderLeft: '1px solid rgba(232, 168, 56, 0.15)',
          }}
        >
          <DrawerHeader>
            <DrawerHeaderTitle
              action={
                <Button
                  appearance="subtle"
                  icon={<Dismiss24Regular />}
                  onClick={() => setTelemetryOpen(false)}
                />
              }
            >
              📡 Real-Time Telemetry
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody>
            <TelemetryPanel
              resetKey={chatKey}
              onClose={() => setTelemetryOpen(false)}
            />
          </DrawerBody>
        </Drawer>
      </main>
    </div>
  );
}
