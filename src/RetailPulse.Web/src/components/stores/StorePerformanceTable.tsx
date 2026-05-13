import { useState, useMemo, useCallback } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { STORE_COLORS } from '../../constants/agentRouting';
import type { StorePerformance, PerformanceLevel } from '../../types';

interface StorePerformanceTableProps {
  stores: StorePerformance[];
  onStoreClick?: (storeId: string) => void;
}

type SortKey = 'storeName' | 'region' | 'revenue' | 'target' | 'performanceIndex' | 'issues' | 'recommendations';
type SortDir = 'asc' | 'desc';

function getPerformanceLevel(store: StorePerformance): PerformanceLevel {
  const pct = store.target > 0 ? (store.revenue / store.target) * 100 : 0;
  if (pct >= 100) return 'green';
  if (pct >= 80) return 'yellow';
  return 'red';
}

function formatCurrency(value: number): string {
  if (value >= 1_000_000) return `$${(value / 1_000_000).toFixed(1)}M`;
  if (value >= 1_000) return `$${(value / 1_000).toFixed(0)}k`;
  return `$${value.toFixed(0)}`;
}

const LEVEL_COLOR: Record<PerformanceLevel, string> = {
  green: STORE_COLORS.green,
  yellow: STORE_COLORS.yellow,
  red: STORE_COLORS.red,
};

const LEVEL_BG: Record<PerformanceLevel, string> = {
  green: STORE_COLORS.greenBg,
  yellow: STORE_COLORS.yellowBg,
  red: STORE_COLORS.redBg,
};

const COLUMNS: { key: SortKey; label: string }[] = [
  { key: 'storeName', label: 'Store Name' },
  { key: 'region', label: 'Region' },
  { key: 'revenue', label: 'Revenue' },
  { key: 'target', label: 'Target' },
  { key: 'performanceIndex', label: 'Performance' },
  { key: 'issues', label: 'Issues' },
  { key: 'recommendations', label: 'Recommendations' },
];

function getSortValue(store: StorePerformance, key: SortKey): string | number {
  switch (key) {
    case 'storeName': return store.storeName.toLowerCase();
    case 'region': return store.region.toLowerCase();
    case 'revenue': return store.revenue;
    case 'target': return store.target;
    case 'performanceIndex': return store.performanceIndex;
    case 'issues': return store.issues.length;
    case 'recommendations': return store.recommendations[0]?.toLowerCase() ?? '';
  }
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '4px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: STORE_COLORS.green,
  },
  tableWrap: {
    overflowX: 'auto',
    borderRadius: '10px',
    border: `1px solid ${STORE_COLORS.cardBorder}`,
    background: STORE_COLORS.cardBg,
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  th: {
    textAlign: 'left',
    padding: '10px 14px',
    fontSize: '11px',
    fontWeight: '700',
    color: 'var(--color-text-muted, rgba(255,255,255,0.55))',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    borderBottom: `1px solid ${STORE_COLORS.cardBorder}`,
    cursor: 'pointer',
    userSelect: 'none',
    whiteSpace: 'nowrap',
    transition: 'color 0.15s ease',
    ':hover': {
      color: 'var(--color-text, #e2e8f0)',
    },
  },
  thActive: {
    color: '#fff',
  },
  sortArrow: {
    marginLeft: '4px',
    fontSize: '10px',
  },
  tr: {
    transition: 'background 0.15s ease',
    ':hover': {
      background: STORE_COLORS.heatmapHover,
    },
  },
  td: {
    padding: '10px 14px',
    borderBottom: `1px solid ${STORE_COLORS.gridLine}`,
    color: 'var(--color-text, #e2e8f0)',
  },
  storeLink: {
    fontWeight: '600',
    cursor: 'pointer',
    color: '#60a5fa',
    ':hover': {
      textDecoration: 'underline',
    },
  },
  perfBadge: {
    display: 'inline-block',
    padding: '2px 10px',
    borderRadius: '4px',
    fontWeight: '700',
    fontSize: '12px',
  },
  issuesBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    minWidth: '24px',
    padding: '2px 8px',
    borderRadius: '10px',
    fontWeight: '700',
    fontSize: '11px',
    background: 'rgba(239,68,68,0.15)',
    color: '#fca5a5',
  },
  recText: {
    fontSize: '12px',
    color: 'var(--color-text-muted, rgba(255,255,255,0.6))',
    maxWidth: '220px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted, rgba(255,255,255,0.5))',
    fontSize: '14px',
  },
});

export function StorePerformanceTable({ stores, onStoreClick }: StorePerformanceTableProps) {
  const styles = useStyles();
  const [sortKey, setSortKey] = useState<SortKey>('performanceIndex');
  const [sortDir, setSortDir] = useState<SortDir>('desc');

  const handleSort = useCallback((key: SortKey) => {
    setSortKey(prev => {
      if (prev === key) {
        setSortDir(d => (d === 'asc' ? 'desc' : 'asc'));
        return key;
      }
      setSortDir('asc');
      return key;
    });
  }, []);

  const sorted = useMemo(() => {
    return [...stores].sort((a, b) => {
      const va = getSortValue(a, sortKey);
      const vb = getSortValue(b, sortKey);
      const cmp = typeof va === 'number' && typeof vb === 'number'
        ? va - vb
        : String(va).localeCompare(String(vb));
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [stores, sortKey, sortDir]);

  if (stores.length === 0) {
    return (
      <div data-testid="store-performance-table">
        <div className={styles.titleRow}>
          <span className={styles.title}>📊 Store Performance</span>
        </div>
        <div className={styles.empty} data-testid="table-empty">No store data available</div>
      </div>
    );
  }

  return (
    <div data-testid="store-performance-table" className={styles.container}>
      <div className={styles.titleRow}>
        <span className={styles.title}>📊 Store Performance</span>
      </div>
      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              {COLUMNS.map(col => (
                <th
                  key={col.key}
                  className={`${styles.th} ${sortKey === col.key ? styles.thActive : ''}`}
                  onClick={() => handleSort(col.key)}
                  data-testid={`sort-${col.key}`}
                  aria-sort={sortKey === col.key ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
                >
                  {col.label}
                  {sortKey === col.key && (
                    <span className={styles.sortArrow}>
                      {sortDir === 'asc' ? '▲' : '▼'}
                    </span>
                  )}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sorted.map(store => {
              const level = getPerformanceLevel(store);
              return (
                <tr key={store.storeId} className={styles.tr} data-testid="table-row">
                  <td className={styles.td}>
                    <span
                      className={styles.storeLink}
                      role="button"
                      tabIndex={0}
                      onClick={() => onStoreClick?.(store.storeId)}
                      onKeyDown={e => { if (e.key === 'Enter') onStoreClick?.(store.storeId); }}
                    >
                      {store.storeName}
                    </span>
                  </td>
                  <td className={styles.td}>{store.region}</td>
                  <td className={styles.td}>{formatCurrency(store.revenue)}</td>
                  <td className={styles.td}>{formatCurrency(store.target)}</td>
                  <td className={styles.td}>
                    <span
                      className={styles.perfBadge}
                      style={{
                        background: LEVEL_BG[level],
                        color: LEVEL_COLOR[level],
                      }}
                    >
                      {store.performanceIndex.toFixed(0)}%
                    </span>
                  </td>
                  <td className={styles.td}>
                    {store.issues.length > 0 ? (
                      <span className={styles.issuesBadge}>{store.issues.length}</span>
                    ) : (
                      <span style={{ color: STORE_COLORS.green, fontSize: '12px' }}>✓</span>
                    )}
                  </td>
                  <td className={styles.td}>
                    <span className={styles.recText}>
                      {store.recommendations[0] ?? '—'}
                    </span>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
