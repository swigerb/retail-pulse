import { useState, useEffect } from 'react';
import { makeStyles } from '@fluentui/react-components';
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
  filterSelect: {
    padding: '8px 14px',
    borderRadius: '8px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    backgroundColor: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    cursor: 'pointer',
    outline: 'none',
    fontWeight: '500',
    minWidth: '140px',
    transition: 'border-color 0.2s ease',
    ':focus': {
    },
  },
  filterInput:{
    padding: '8px 14px',
    borderRadius: '8px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    backgroundColor: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    outline: 'none',
    fontWeight: '500',
    transition: 'border-color 0.2s ease',
    ':focus': {
    },
  },
  searchInput:{
    padding: '8px 14px',
    borderRadius: '8px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    backgroundColor: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    outline: 'none',
    fontWeight: '500',
    flex: '1',
    minWidth: '200px',
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
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  tableHead: {
    textAlign: 'left',
    padding: '12px 14px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    borderBottom: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    fontWeight: '600',
    background: 'rgba(255,255,255,0.02)',
  },
  tableRow: {
    cursor: 'pointer',
    transition: 'background 0.15s ease',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.03)',
    },
  },
  tableCell: {
    padding: '10px 14px',
    color: 'var(--color-text)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'top',
  },
  tableCellMuted: {
    padding: '10px 14px',
    color: 'var(--color-text-muted)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'top',
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
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    fontWeight: '600',
  },
  detailText: {
    fontSize: '13px',
    color: 'var(--color-text)',
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
    color: 'var(--color-text-muted)',
  },
  pageButtons: {
    display: 'flex',
    gap: '8px',
  },
  pageBtn: {
    padding: '6px 16px',
    borderRadius: '8px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    background: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
    ':disabled':{
      opacity: 0.4,
      cursor: 'default',
    },
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
    color: 'var(--color-text-muted)',
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
    let cancelled = false;
    fetchAuditLog(filters, page, PAGE_SIZE)
      .then(result => {
        if (cancelled) return;
        setEntries(result.entries);
        setTotalCount(result.totalCount);
        setError(null);
        setLoading(false);
      })
      .catch(e => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : 'Failed to load audit log');
        setLoading(false);
      });
    return () => { cancelled = true; };
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
        <select
          className={styles.filterSelect}
          value={filters.agent ?? ''}
          onChange={e => updateFilter('agent', e.target.value)}
          data-testid="filter-agent"
          aria-label="Filter by agent"
        >
          <option value="">All Agents</option>
          <option value="DemandAgent">Demand Agent</option>
          <option value="SupplyAgent">Supply Agent</option>
          <option value="PromoAgent">Promo Agent</option>
          <option value="CompetitiveAgent">Competitive Agent</option>
          <option value="SentimentAgent">Sentiment Agent</option>
          <option value="OrchestratorAgent">Orchestrator</option>
        </select>
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
        <select
          className={styles.filterSelect}
          value={filters.actionType ?? ''}
          onChange={e => updateFilter('actionType', e.target.value)}
          data-testid="filter-action"
          aria-label="Filter by action type"
        >
          <option value="">All Actions</option>
          <option value="query">Query</option>
          <option value="tool_call">Tool Call</option>
          <option value="approval">Approval</option>
          <option value="escalation">Escalation</option>
        </select>
        <input
          type="text"
          className={styles.searchInput}
          placeholder="🔍 Search logs..."
          value={filters.searchText ?? ''}
          onChange={e => updateFilter('searchText', e.target.value)}
          data-testid="filter-search"
          aria-label="Search audit logs"
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
            <table className={styles.table} data-testid="audit-table">
              <thead>
                <tr>
                  <th className={styles.tableHead}>Timestamp</th>
                  <th className={styles.tableHead}>User</th>
                  <th className={styles.tableHead}>Agent</th>
                  <th className={styles.tableHead}>Action</th>
                  <th className={styles.tableHead}>Tokens</th>
                  <th className={styles.tableHead}>Duration</th>
                </tr>
              </thead>
              <tbody>
                {entries.map(entry => (
                  <>
                    <tr
                      key={entry.id}
                      className={styles.tableRow}
                      onClick={() => toggleRow(entry.id)}
                      data-testid={`audit-row-${entry.id}`}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') toggleRow(entry.id); }}
                      aria-expanded={expandedId === entry.id}
                    >
                      <td className={styles.tableCellMuted}>{formatTimestamp(entry.timestamp)}</td>
                      <td className={styles.tableCell}>{entry.userName}</td>
                      <td className={styles.tableCell}>
                        <span className={styles.agentPill}>{entry.agentName}</span>
                      </td>
                      <td className={styles.tableCell}>{entry.action}</td>
                      <td className={styles.tableCellMuted}>{entry.tokens.toLocaleString()}</td>
                      <td className={styles.tableCellMuted}>{entry.durationMs}ms</td>
                    </tr>
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
              </tbody>
            </table>
          </div>

          {/* Pagination */}
          <div className={styles.pagination} data-testid="audit-pagination">
            <span className={styles.pageInfo}>
              Page {page} of {totalPages} ({totalCount.toLocaleString()} entries)
            </span>
            <div className={styles.pageButtons}>
              <button
                className={styles.pageBtn}
                onClick={() => setPage(p => Math.max(1, p - 1))}
                disabled={page <= 1}
                data-testid="page-prev"
              >
                ← Previous
              </button>
              <button
                className={styles.pageBtn}
                onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages}
                data-testid="page-next"
              >
                Next →
              </button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
