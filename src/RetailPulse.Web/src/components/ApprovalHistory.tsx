import { useState, useMemo } from 'react';
import {
  Input,
  Badge,
  Text,
  makeStyles,
} from '@fluentui/react-components';
import { Search24Regular } from '@fluentui/react-icons';
import type { ApprovalRequest, ApprovalDecision } from '../types';

export interface ApprovalHistoryProps {
  approvals: ApprovalRequest[];
}

const DECISION_CONFIG: Record<ApprovalDecision, { emoji: string; label: string; color: string; badge: 'success' | 'danger' | 'informative' | 'warning' | 'subtle' }> = {
  approved: { emoji: '✅', label: 'Approved', color: '#22c55e', badge: 'success' },
  rejected: { emoji: '❌', label: 'Rejected', color: '#ef4444', badge: 'danger' },
  modified: { emoji: '✏️', label: 'Modified', color: '#3b82f6', badge: 'informative' },
  timed_out: { emoji: '⏰', label: 'Timed Out', color: '#6b7280', badge: 'warning' },
  pending: { emoji: '⏳', label: 'Pending', color: '#eab308', badge: 'subtle' },
};

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    backgroundColor: 'var(--color-surface)',
    borderRadius: '12px',
    border: '1px solid var(--color-border)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '16px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  filtersRow: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  filterChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '4px 10px',
    borderRadius: '16px',
    fontSize: '11px',
    fontWeight: '500',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    border: '1px solid var(--color-border)',
    backgroundColor: 'transparent',
    color: 'var(--color-text-muted)',
    ':hover': {
      backgroundColor: 'var(--color-surface-hover)',
    },
  },
  filterChipActive: {
    backgroundColor: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
    color: 'var(--brand-accent)',
    fontWeight: '600',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '12px',
  },
  th: {
    textAlign: 'left',
    padding: '8px 10px',
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: 'var(--color-text-subtle)',
    borderBottom: '1px solid var(--color-border)',
  },
  td: {
    padding: '8px 10px',
    color: 'var(--color-text)',
    borderBottom: '1px solid var(--color-border-faint)',
    verticalAlign: 'top',
  },
  actionCell: {
    maxWidth: '200px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '24px 16px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
  },
});

function formatShortDate(dateStr?: string): string {
  if (!dateStr) return '—';
  const d = new Date(dateStr);
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function ApprovalHistory({ approvals }: ApprovalHistoryProps) {
  const [search, setSearch] = useState('');
  const [decisionFilter, setDecisionFilter] = useState<ApprovalDecision | null>(null);
  const styles = useStyles();

  const filtered = useMemo(() => {
    let result = approvals;
    if (decisionFilter) result = result.filter(a => a.status === decisionFilter);
    if (search.trim()) {
      const q = search.toLowerCase();
      result = result.filter(a =>
        a.action.toLowerCase().includes(q) ||
        a.agentName.toLowerCase().includes(q) ||
        a.reasoning.toLowerCase().includes(q)
      );
    }
    return [...result].sort((a, b) =>
      new Date(b.requestedAt).getTime() - new Date(a.requestedAt).getTime()
    );
  }, [approvals, decisionFilter, search]);

  return (
    <div className={styles.panel} data-testid="approval-history">
      <div className={styles.header}>
        <span className={styles.title}>
          📋 Approval History
          <Badge appearance="filled" color="informative">{approvals.length}</Badge>
        </span>
      </div>

      <div className={styles.filtersRow}>
        <Search24Regular style={{ color: 'var(--color-text-muted)', flexShrink: 0, fontSize: '16px' }} />
        <Input
          value={search}
          onChange={(_e, data) => setSearch(data.value)}
          placeholder="Search approvals..."
          size="small"
          style={{ flex: 1, minWidth: '120px' }}
        />
      </div>

      <div className={styles.filtersRow}>
        <button
          className={`${styles.filterChip} ${decisionFilter === null ? styles.filterChipActive : ''}`}
          onClick={() => setDecisionFilter(null)}
        >
          All
        </button>
        {(Object.keys(DECISION_CONFIG) as ApprovalDecision[])
          .filter(d => d !== 'pending')
          .map(decision => {
            const config = DECISION_CONFIG[decision];
            return (
              <button
                key={decision}
                className={`${styles.filterChip} ${decisionFilter === decision ? styles.filterChipActive : ''}`}
                onClick={() => setDecisionFilter(decisionFilter === decision ? null : decision)}
              >
                {config.emoji} {config.label}
              </button>
            );
          })}
      </div>

      {filtered.length === 0 ? (
        <div className={styles.emptyState} data-testid="history-empty">
          <Text>No approval records found</Text>
        </div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th className={styles.th}>Action</th>
                <th className={styles.th}>Agent</th>
                <th className={styles.th}>Decision</th>
                <th className={styles.th}>By</th>
                <th className={styles.th}>When</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map(approval => {
                const config = DECISION_CONFIG[approval.status] ?? DECISION_CONFIG.pending;
                return (
                  <tr key={approval.id} data-testid="history-row">
                    <td className={`${styles.td} ${styles.actionCell}`} title={approval.action}>
                      {approval.action}
                    </td>
                    <td className={styles.td}>{approval.agentName}</td>
                    <td className={styles.td}>
                      <Badge appearance="tint" color={config.badge} size="small">
                        {config.emoji} {config.label}
                      </Badge>
                    </td>
                    <td className={styles.td}>{approval.decidedBy ?? '—'}</td>
                    <td className={styles.td}>{formatShortDate(approval.decidedAt ?? approval.requestedAt)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
