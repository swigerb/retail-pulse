import { useState, useMemo, useCallback } from 'react';
import {
  makeStyles,
  Badge,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  tokens,
} from '@fluentui/react-components';
import { ArrowUp16Filled, ArrowDown16Filled } from '@fluentui/react-icons';
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
  sortableHeader: {
    cursor: 'pointer',
    userSelect: 'none',
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
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
  recText: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    maxWidth: '220px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
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
        <Table size="small">
          <TableHeader>
            <TableRow>
              {COLUMNS.map(col => (
                <TableHeaderCell
                  key={col.key}
                  onClick={() => handleSort(col.key)}
                  data-testid={`sort-${col.key}`}
                  aria-sort={sortKey === col.key ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
                >
                  <span className={styles.sortableHeader}>
                    {col.label}
                    {sortKey === col.key && (
                      sortDir === 'asc' ? <ArrowUp16Filled /> : <ArrowDown16Filled />
                    )}
                  </span>
                </TableHeaderCell>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {sorted.map(store => {
              const level = getPerformanceLevel(store);
              return (
                <TableRow key={store.storeId} data-testid="table-row">
                  <TableCell>
                    <span
                      className={styles.storeLink}
                      role="button"
                      tabIndex={0}
                      onClick={() => onStoreClick?.(store.storeId)}
                      onKeyDown={e => { if (e.key === 'Enter') onStoreClick?.(store.storeId); }}
                    >
                      {store.storeName}
                    </span>
                  </TableCell>
                  <TableCell>{store.region}</TableCell>
                  <TableCell>{formatCurrency(store.revenue)}</TableCell>
                  <TableCell>{formatCurrency(store.target)}</TableCell>
                  <TableCell>
                    <span
                      className={styles.perfBadge}
                      style={{
                        background: LEVEL_BG[level],
                        color: LEVEL_COLOR[level],
                      }}
                    >
                      {store.performanceIndex.toFixed(0)}%
                    </span>
                  </TableCell>
                  <TableCell>
                    {store.issues.length > 0 ? (
                      <Badge appearance="tint" color="danger" size="small">
                        {store.issues.length}
                      </Badge>
                    ) : (
                      <span style={{ color: STORE_COLORS.green, fontSize: '12px' }}>✓</span>
                    )}
                  </TableCell>
                  <TableCell>
                    <span className={styles.recText}>
                      {store.recommendations[0] ?? '—'}
                    </span>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
