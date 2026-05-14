import { useState, useEffect } from 'react';
import {
  makeStyles,
  Dropdown,
  Option,
  Input,
  Button,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  tokens,
} from '@fluentui/react-components';
import { OBSERVABILITY_COLORS } from '../../constants/agentRouting';
import { fetchAuditLog } from '../../services/observabilityApi';
import type { AuditLogFilters, AuditLogEntry } from '../../types';

const PAGE_SIZE = 50;

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  filterBar: {
    display: 'flex',
    gap: '10px',
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  filterInput:{
    padding: '8px 14px',
    borderRadius: '8px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    backgroundColor: 'rgba(255,255,255,0.04)',
    color: tokens.colorNeutralForeground1,
    fontSize: '13px',
    outline: 'none',
    fontWeight: '500',
    transition: 'border-color 0.2s ease',
    ':focus': {
    },
  },
  tableWrapper:{
    background: OBSERVABILITY_COLORS.cardBg,
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '12px',
    overflow: 'hidden',
  },
  expandedRow: {
    background: 'rgba(255,255,255,0.02)',
  },
  expandedCell: {
    padding: '0 14px 14px 14px',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
  },
  detailSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '8px',
    marginTop: '4px',
  },
  detailLabel: {
    fontSize: '11px',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    fontWeight: '600',
  },
  detailText: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground1,
    lineHeight: '1.6',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  pagination: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: '4px 0',
  },
  pageInfo: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground3,
  },
  pageButtons: {
    display: 'flex',
    gap: '8px',
  },
  skeleton: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  skeletonRow: {
    height: '42px',
    borderRadius: '8px',
    background: 'rgba(255,255,255,0.04)',
    animationName: {
      '0%, 100%': { opacity: 0.4 },
      '50%': { opacity: 0.8 },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
  },
  error: {
    padding: '16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(211,47,47,0.1)',
    border: '1px solid rgba(211,47,47,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '60px 20px',
    color: tokens.colorNeutralForeground3,
    fontSize: '14px',
    gap: '8px',
  },
  agentPill: {
    display: 'inline-block',
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '10px',
    background: `${OBSERVABILITY_COLORS.primary}20`,
    color: OBSERVABILITY_COLORS.primary,
    fontWeight: '600',
  },
  mutedCell: {
    color: tokens.colorNeutralForeground3,
  },
});

function formatTimestamp(ts: string): string {
  const d = new Date(ts);
  return d.toLocaleString(undefined, {
    month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

export default function AuditLogViewer() {
  const styles = useStyles();
  const [filters, setFilters] = useState<AuditLogFilters>({});
  const [page, setPage] = useState(1);
  // Track fetch generation to detect stale responses
  const [fetchGen, setFetchGen] = useState(0);
  const [entries, setEntries] = useState<AuditLogEntry[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  useEffect(() => {
    const controller = new AbortController();
    fetchAuditLog(filters, page, PAGE_SIZE, controller.signal)
      .then(result => {
        setEntries(result.entries);
        setTotalCount(result.totalCount);
        setError(null);
        setLoading(false);
      })
      .catch(e => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to load audit log');
        setLoading(false);
      });
    return () => { controller.abort(); };
  }, [filters, page, fetchGen]);

  const updateFilter = <K extends keyof AuditLogFilters>(key: K, value: AuditLogFilters[K]) => {
    setFilters(prev => ({ ...prev, [key]: value || undefined }));
    setPage(1);
    setLoading(true);
    setError(null);
    setFetchGen(g => g + 1);
  };

  const toggleRow = (id: string) => {
    setExpandedId(prev => prev === id ? null : id);
  };

  return (
    <div className={styles.container} data-testid="audit-log-viewer">
      {/* Filter Bar */}
      <div className={styles.filterBar} data-testid="audit-filters">
        <Dropdown
          value={filters.agent || 'All Agents'}
          selectedOptions={[filters.agent ?? '']}
          onOptionSelect={(_, data) => updateFilter('agent', data.optionValue ?? '')}
          data-testid="filter-agent"
          aria-label="Filter by agent"
          size="small"
        >
          <Option value="">All Agents</Option>
          <Option value="DemandAgent">Demand Agent</Option>
          <Option value="SupplyAgent">Supply Agent</Option>
          <Option value="PromoAgent">Promo Agent</Option>
          <Option value="CompetitiveAgent">Competitive Agent</Option>
          <Option value="SentimentAgent">Sentiment Agent</Option>
          <Option value="OrchestratorAgent">Orchestrator</Option>
        </Dropdown>
        <input
          type="date"
          className={styles.filterInput}
          value={filters.startDate ?? ''}
          onChange={e => updateFilter('startDate', e.target.value)}
          data-testid="filter-start-date"
          aria-label="Start date"
        />
        <input
          type="date"
          className={styles.filterInput}
          value={filters.endDate ?? ''}
          onChange={e => updateFilter('endDate', e.target.value)}
          data-testid="filter-end-date"
          aria-label="End date"
        />
        <Dropdown
          value={filters.actionType || 'All Actions'}
          selectedOptions={[filters.actionType ?? '']}
          onOptionSelect={(_, data) => updateFilter('actionType', data.optionValue ?? '')}
          data-testid="filter-action"
          aria-label="Filter by action type"
          size="small"
        >
          <Option value="">All Actions</Option>
          <Option value="query">Query</Option>
          <Option value="tool_call">Tool Call</Option>
          <Option value="approval">Approval</Option>
          <Option value="escalation">Escalation</Option>
        </Dropdown>
        <Input
          placeholder="🔍 Search logs..."
          value={filters.searchText ?? ''}
          onChange={(_e, data) => updateFilter('searchText', data.value)}
          data-testid="filter-search"
          aria-label="Search audit logs"
          style={{ flex: 1, minWidth: '200px' }}
          size="small"
        />
      </div>

      {error && (
        <div className={styles.error} data-testid="audit-error">⚠️ {error}</div>
      )}

      {loading && (
        <div className={styles.skeleton} data-testid="audit-skeleton">
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className={styles.skeletonRow} style={{ animationDelay: `${i * 0.1}s` }} />
          ))}
        </div>
      )}

      {!loading && entries.length === 0 && !error && (
        <div className={styles.emptyState}>
          <span style={{ fontSize: '32px' }}>📋</span>
          <span>No audit log entries found</span>
        </div>
      )}

      {!loading && entries.length > 0 && (
        <>
          <div className={styles.tableWrapper}>
            <Table size="small" data-testid="audit-table">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Timestamp</TableHeaderCell>
                  <TableHeaderCell>User</TableHeaderCell>
                  <TableHeaderCell>Agent</TableHeaderCell>
                  <TableHeaderCell>Action</TableHeaderCell>
                  <TableHeaderCell>Tokens</TableHeaderCell>
                  <TableHeaderCell>Duration</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {entries.map(entry => (
                  <>
                    <TableRow
                      key={entry.id}
                      onClick={() => toggleRow(entry.id)}
                      data-testid={`audit-row-${entry.id}`}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') toggleRow(entry.id); }}
                      aria-expanded={expandedId === entry.id}
                      style={{ cursor: 'pointer' }}
                    >
                      <TableCell className={styles.mutedCell}>{formatTimestamp(entry.timestamp)}</TableCell>
                      <TableCell>{entry.userName}</TableCell>
                      <TableCell>
                        <span className={styles.agentPill}>{entry.agentName}</span>
                      </TableCell>
                      <TableCell>{entry.action}</TableCell>
                      <TableCell className={styles.mutedCell}>{(entry.tokens ?? 0).toLocaleString()}</TableCell>
                      <TableCell className={styles.mutedCell}>{entry.durationMs ?? 0}ms</TableCell>
                    </TableRow>
                    {expandedId === entry.id && (
                      <tr key={`${entry.id}-detail`} className={styles.expandedRow}>
                        <td className={styles.expandedCell} colSpan={6}>
                          <div className={styles.detailSection}>
                            <div>
                              <div className={styles.detailLabel}>Input Summary</div>
                              <div className={styles.detailText}>{entry.inputSummary || '—'}</div>
                            </div>
                            <div>
                              <div className={styles.detailLabel}>Output Summary</div>
                              <div className={styles.detailText}>{entry.outputSummary || '—'}</div>
                            </div>
                          </div>
                        </td>
                      </tr>
                    )}
                  </>
                ))}
              </TableBody>
            </Table>
          </div>

          {/* Pagination */}
          <div className={styles.pagination} data-testid="audit-pagination">
            <span className={styles.pageInfo}>
              Page {page} of {totalPages} ({totalCount.toLocaleString()} entries)
            </span>
            <div className={styles.pageButtons}>
              <Button
                appearance="subtle"
                size="small"
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                data-testid="page-prev"
              >
                ← Previous
              </Button>
              <Button
                appearance="subtle"
                size="small"
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                data-testid="page-next"
              >
                Next →
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
