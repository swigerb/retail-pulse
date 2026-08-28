import { useMemo } from 'react';
import {
  ResponsiveContainer,
  AreaChart,
  Area,
  BarChart,
  Bar,
  Cell,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
} from 'recharts';
import { makeStyles, Badge } from '@fluentui/react-components';
import { COMPETITIVE_COLORS } from '../../constants/agentRouting';
import type { MarketShareEntry } from '../../types';

interface MarketShareChartProps {
  data: MarketShareEntry[];
  compact?: boolean;
}

const AXIS_TICK = { fill: '#94a3b8', fontSize: 11 } as const;

const tooltipContentStyle = {
  backgroundColor: '#1e1b2e',
  border: '1px solid rgba(59,130,246,0.3)',
  borderRadius: 8,
  color: '#f1f5f9',
  fontSize: 12,
} as const;

const COMPETITOR_PALETTE = ['#6b7280', '#9ca3af', '#4b5563', '#78716c', '#a1a1aa', '#737373'];

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
    borderRadius: '12px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '16px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#3b82f6',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
});

export default function MarketShareChart({ data, compact }: MarketShareChartProps) {
  const styles = useStyles();

  const { chartData, brands } = useMemo(() => {
    const quarters = [...new Set(data.map(d => d.quarter))].sort();
    const brandSet = [...new Set(data.map(d => d.brand))];

    const rows = quarters.map(q => {
      const row: Record<string, string | number> = { quarter: q };
      for (const b of brandSet) {
        const entry = data.find(d => d.quarter === q && d.brand === b);
        row[b] = entry?.share ?? 0;
      }
      return row;
    });

    return { chartData: rows, brands: brandSet, quarters };
  }, [data]);

  /**
   * A trend needs at least two periods and a readable number of series.
   *
   * The live feed carries a single quarter across 41 brands, which an area chart draws as
   * one vertical stripe of 41 overlapping points: technically correct, completely
   * unreadable. When there is nothing to trend, rank instead, which is the honest view of
   * a single-period snapshot.
   */
  const { isSnapshot, snapshotRows } = useMemo(() => {
    const periods = new Set(data.map(d => d.quarter)).size;
    if (periods > 1) return { isSnapshot: false, snapshotRows: [] };

    const rows = [...data]
      .sort((a, b) => b.share - a.share)
      .slice(0, compact ? 8 : 12)
      .map(d => ({ brand: d.brand, share: d.share, isOurBrand: d.isOurBrand }));

    return { isSnapshot: true, snapshotRows: rows };
  }, [data, compact]);

  const ourBrands = useMemo(() => {
    const set = new Set(data.filter(d => d.isOurBrand).map(d => d.brand));
    return set;
  }, [data]);

  if (data.length === 0) {
    return (
      <div className={styles.wrapper}>
        <div className={styles.titleRow}>
          <span className={styles.title}>📈 Market Share Trends</span>
        </div>
        <div className={styles.empty} data-testid="market-share-empty">No market share data available</div>
      </div>
    );
  }

  let competitorIdx = 0;

  return (
    <div className={styles.wrapper} data-testid="market-share-chart">
      <div className={styles.titleRow}>
        <span className={styles.title}>
          {isSnapshot ? '📊 Market Share by Brand' : '📈 Market Share Trends'}
        </span>
        <Badge appearance="filled" style={{ background: 'rgba(59,130,246,0.15)', color: '#93c5fd' }}>
          {isSnapshot ? `Top ${snapshotRows.length} of ${brands.length}` : `${brands.length} brands`}
        </Badge>
      </div>

      {isSnapshot ? (
        <ResponsiveContainer width="100%" height={compact ? 240 : 380}>
          <BarChart
            data={snapshotRows}
            layout="vertical"
            margin={{ top: 8, right: 24, bottom: 8, left: 8 }}
          >
            <CartesianGrid strokeDasharray="3 3" stroke={COMPETITIVE_COLORS.gridLine} horizontal={false} />
            <XAxis type="number" tick={AXIS_TICK} unit="%" />
            <YAxis type="category" dataKey="brand" tick={AXIS_TICK} width={130} />
            <Tooltip
              contentStyle={tooltipContentStyle}
              formatter={(v) => [`${Number(v).toFixed(1)}%`, 'Share']}
            />
            <Bar dataKey="share" radius={[0, 4, 4, 0]}>
              {snapshotRows.map(row => (
                <Cell
                  key={row.brand}
                  fill={row.isOurBrand ? COMPETITIVE_COLORS.ourBrand : COMPETITOR_PALETTE[0]}
                />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      ) : (
        <ResponsiveContainer width="100%" height={compact ? 220 : 360}>
        <AreaChart data={chartData} margin={{ top: 10, right: 20, bottom: 24, left: 10 }}>
          <defs>
            {brands.map(b => {
              const isOurs = ourBrands.has(b);
              const color = isOurs ? COMPETITIVE_COLORS.ourBrand : COMPETITOR_PALETTE[competitorIdx++ % COMPETITOR_PALETTE.length];
              return (
                <linearGradient key={b} id={`share-${b.replace(/\s+/g, '-')}`} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor={color} stopOpacity={isOurs ? 0.4 : 0.15} />
                  <stop offset="100%" stopColor={color} stopOpacity={0.02} />
                </linearGradient>
              );
            })}
          </defs>
          <CartesianGrid strokeDasharray="3 3" stroke={COMPETITIVE_COLORS.gridLine} />
          <XAxis dataKey="quarter" tick={AXIS_TICK} />
          <YAxis tick={AXIS_TICK} label={{ value: 'Share %', fill: '#94a3b8', angle: -90, position: 'insideLeft', fontSize: 11 }} />
          <Tooltip contentStyle={tooltipContentStyle} formatter={(v) => [`${Number(v).toFixed(1)}%`]} />
          {!compact && <Legend wrapperStyle={{ color: '#94a3b8', fontSize: 11, paddingTop: 12 }} />}
          {(() => { competitorIdx = 0; return null; })()}
          {brands.map(b => {
            const isOurs = ourBrands.has(b);
            const color = isOurs ? COMPETITIVE_COLORS.ourBrand : COMPETITOR_PALETTE[competitorIdx++ % COMPETITOR_PALETTE.length];
            return (
              <Area
                key={b}
                type="monotone"
                dataKey={b}
                stackId="1"
                stroke={color}
                strokeWidth={isOurs ? 2.5 : 1}
                fill={`url(#share-${b.replace(/\s+/g, '-')})`}
                fillOpacity={1}
                isAnimationActive={true}
                animationDuration={800}
              />
            );
          })}
        </AreaChart>
      </ResponsiveContainer>
      )}
    </div>
  );
}
