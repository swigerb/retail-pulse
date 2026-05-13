import { useState } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { OBSERVABILITY_COLORS } from '../../constants/agentRouting';
import CostDashboard from './CostDashboard';
import AuditLogViewer from './AuditLogViewer';
import ConversationExport from './ConversationExport';

type TabKey = 'cost' | 'audit' | 'export';

const TABS: { key: TabKey; label: string; icon: string }[] = [
  { key: 'cost', label: 'Cost Dashboard', icon: '💰' },
  { key: 'audit', label: 'Audit Log', icon: '📋' },
  { key: 'export', label: 'Export', icon: '📤' },
];

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'auto',
    padding: '24px',
    backgroundColor: 'var(--color-bg)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    marginBottom: '8px',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: OBSERVABILITY_COLORS.primary,
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    letterSpacing: '-0.5px',
  },
  subtitle: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    fontWeight: '500',
    marginBottom: '20px',
  },
  tabBar: {
    display: 'flex',
    gap: '0',
    borderBottom: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    marginBottom: '24px',
  },
  tab: {
    padding: '12px 22px',
    border: 'none',
    borderBottom: '2px solid transparent',
    background: 'transparent',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.25s ease',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    ':hover': {
      color: 'var(--color-text)',
      background: 'rgba(255,255,255,0.03)',
    },
  },
  tabActive: {
    padding: '12px 22px',
    border: 'none',
    borderBottom: `2px solid ${OBSERVABILITY_COLORS.tabActive}`,
    background: 'transparent',
    color: OBSERVABILITY_COLORS.tabActive,
    fontSize: '14px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.25s ease',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  content: {
    flex: 1,
    minHeight: 0,
  },
});

export default function ObservabilityPanel() {
  const styles = useStyles();
  const [activeTab, setActiveTab] = useState<TabKey>('cost');

  return (
    <div className={styles.container} data-testid="observability-panel">
      <div className={styles.header}>
        <div className={styles.title}>🔭 Observability</div>
      </div>
      <div className={styles.subtitle}>AI Cost Tracking · Audit Logs · Conversation Export</div>

      <div className={styles.tabBar} role="tablist" data-testid="observability-tabs">
        {TABS.map(tab => (
          <button
            key={tab.key}
            className={activeTab === tab.key ? styles.tabActive : styles.tab}
            onClick={() => setActiveTab(tab.key)}
            role="tab"
            aria-selected={activeTab === tab.key}
            data-testid={`tab-${tab.key}`}
          >
            <span>{tab.icon}</span>
            {tab.label}
          </button>
        ))}
      </div>

      <div className={styles.content} role="tabpanel">
        {activeTab === 'cost' && <CostDashboard />}
        {activeTab === 'audit' && <AuditLogViewer />}
        {activeTab === 'export' && <ConversationExport />}
      </div>
    </div>
  );
}
