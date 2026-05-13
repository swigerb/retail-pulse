import { useMemo, useState } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { STORE_COLORS } from '../../constants/agentRouting';
import type { StorePerformance, PerformanceLevel } from '../../types';

interface StoreHeatmapProps {
  stores: StorePerformance[];
  onStoreClick?: (storeId: string) => void;
}

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

function abbreviate(name: string): string {
  const words = name.split(/\s+/);
  if (words.length === 1) return name.slice(0, 6);
  return words.map(w => w[0]).join('').toUpperCase().slice(0, 4);
}

const LEVEL_BG: Record<PerformanceLevel, string> = {
  green: STORE_COLORS.greenBg,
  yellow: STORE_COLORS.yellowBg,
  red: STORE_COLORS.redBg,
};

const LEVEL_COLOR: Record<PerformanceLevel, string> = {
  green: STORE_COLORS.green,
  yellow: STORE_COLORS.yellow,
  red: STORE_COLORS.red,
};

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
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
  regionSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  regionHeader: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text-muted, rgba(255,255,255,0.55))',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    paddingBottom: '4px',
    borderBottom: `1px solid ${STORE_COLORS.gridLine}`,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))',
    gap: '8px',
  },
  cell: {
    position: 'relative',
    borderRadius: '8px',
    padding: '14px 10px',
    textAlign: 'center',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    border: `1px solid ${STORE_COLORS.cardBorder}`,
    ':hover': {
      transform: 'translateY(-2px)',
      boxShadow: '0 4px 12px rgba(0,0,0,0.3)',
    },
  },
  cellName: {
    fontSize: '13px',
    fontWeight: '700',
    letterSpacing: '0.5px',
  },
  cellIndex: {
    fontSize: '11px',
    marginTop: '4px',
    opacity: 0.8,
  },
  tooltip: {
    position: 'absolute',
    bottom: 'calc(100% + 8px)',
    left: '50%',
    transform: 'translateX(-50%)',
    background: 'rgba(0,0,0,0.92)',
    border: '1px solid rgba(255,255,255,0.12)',
    borderRadius: '8px',
    padding: '10px 14px',
    zIndex: 100,
    whiteSpace: 'nowrap',
    fontSize: '12px',
    color: '#e2e8f0',
    lineHeight: '1.6',
    pointerEvents: 'none',
    boxShadow: '0 8px 24px rgba(0,0,0,0.5)',
  },
  tooltipLabel: {
    fontWeight: '700',
    color: '#fff',
    display: 'block',
    marginBottom: '2px',
    fontSize: '13px',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted, rgba(255,255,255,0.5))',
    fontSize: '14px',
  },
});

export function StoreHeatmap({ stores, onStoreClick }: StoreHeatmapProps) {
  const styles = useStyles();
  const [hoveredId, setHoveredId] = useState<string | null>(null);

  const grouped = useMemo(() => {
    const map = new Map<string, StorePerformance[]>();
    for (const store of stores) {
      const list = map.get(store.region) ?? [];
      list.push(store);
      map.set(store.region, list);
    }
    return Array.from(map.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [stores]);

  if (stores.length === 0) {
    return (
      <div data-testid="store-heatmap">
        <div className={styles.titleRow}>
          <span className={styles.title}>🗺️ Store Performance Heatmap</span>
        </div>
        <div className={styles.empty} data-testid="heatmap-empty">No store data available</div>
      </div>
    );
  }

  return (
    <div data-testid="store-heatmap">
      <div className={styles.titleRow}>
        <span className={styles.title}>🗺️ Store Performance Heatmap</span>
      </div>
      <div className={styles.container}>
        {grouped.map(([region, regionStores]) => (
          <div key={region} className={styles.regionSection}>
            <div className={styles.regionHeader}>{region}</div>
            <div className={styles.grid}>
              {regionStores.map(store => {
                const level = getPerformanceLevel(store);
                return (
                  <div
                    key={store.storeId}
                    className={styles.cell}
                    style={{
                      background: LEVEL_BG[level],
                      borderColor: LEVEL_COLOR[level],
                    }}
                    data-testid="heatmap-cell"
                    role="button"
                    tabIndex={0}
                    aria-label={`${store.storeName} - performance ${store.performanceIndex}`}
                    onClick={() => onStoreClick?.(store.storeId)}
                    onKeyDown={e => { if (e.key === 'Enter') onStoreClick?.(store.storeId); }}
                    onMouseEnter={() => setHoveredId(store.storeId)}
                    onMouseLeave={() => setHoveredId(null)}
                  >
                    <div className={styles.cellName} style={{ color: LEVEL_COLOR[level] }}>
                      {abbreviate(store.storeName)}
                    </div>
                    <div className={styles.cellIndex} style={{ color: LEVEL_COLOR[level] }}>
                      {store.performanceIndex.toFixed(0)}%
                    </div>
                    {hoveredId === store.storeId && (
                      <div className={styles.tooltip} data-testid="heatmap-tooltip">
                        <span className={styles.tooltipLabel}>{store.storeName}</span>
                        <span>Revenue: {formatCurrency(store.revenue)}</span><br />
                        <span>Target: {formatCurrency(store.target)}</span><br />
                        <span>Issues: {store.issues.length}</span>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
