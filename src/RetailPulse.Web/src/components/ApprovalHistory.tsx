import { useState, useMemo } from 'react';
import {
  Input,
  Badge,
  Text,
  makeStyles,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  tokens,
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
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: '12px',
    border: `1px solid ${tokens.colorNeutralStroke1}`,
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
    color: tokens.colorNeutralForeground1,
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
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground3,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  filterChipActive: {
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke1}`,
    color: tokens.colorBrandForeground1,
    fontWeight: '600',
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
    color: tokens.colorNeutralForeground3,
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
        <Search24Regular style={{ color: tokens.colorNeutralForeground3, flexShrink: 0, fontSize: '16px' }} />
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
          <Table size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>Action</TableHeaderCell>
                <TableHeaderCell>Agent</TableHeaderCell>
                <TableHeaderCell>Decision</TableHeaderCell>
                <TableHeaderCell>By</TableHeaderCell>
                <TableHeaderCell>When</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {filtered.map(approval => {
                const config = DECISION_CONFIG[approval.status] ?? DECISION_CONFIG.pending;
                return (
                  <TableRow key={approval.id} data-testid="history-row">
                    <TableCell>
                      <span className={styles.actionCell} title={approval.action}>
                        {approval.action}
                      </span>
                    </TableCell>
                    <TableCell>{approval.agentName}</TableCell>
                    <TableCell>
                      <Badge appearance="tint" color={config.badge} size="small">
                        {config.emoji} {config.label}
                      </Badge>
                    </TableCell>
                    <TableCell>{approval.decidedBy ?? '—'}</TableCell>
                    <TableCell>{formatShortDate(approval.decidedAt ?? approval.requestedAt)}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}
