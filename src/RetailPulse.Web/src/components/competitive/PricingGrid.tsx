import { useMemo } from 'react';
import {
  ResponsiveContainer,
  LineChart,
  Line,
} from 'recharts';
import { makeStyles, Badge } from '@fluentui/react-components';
import { COMPETITIVE_COLORS } from '../../constants/agentRouting';
import type { CompetitorPricing } from '../../types';

interface PricingGridProps {
  data: CompetitorPricing[];
}

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
    color: '#ef4444',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  th: {
    padding: '10px 14px',
    textAlign: 'left',
    borderBottom: '2px solid var(--color-border)',
    color: '#94a3b8',
    fontWeight: '600',
    fontSize: '11px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  td: {
    padding: '10px 14px',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
  },
  row: {
    transition: 'background-color 0.15s',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.04)',
    },
  },
  alertRow: {
    backgroundColor: 'rgba(239,68,68,0.06)',
    ':hover': {
      backgroundColor: 'rgba(239,68,68,0.1)',
    },
  },
  sparkline: {
    display: 'flex',
    alignItems: 'center',
    height: '32px',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
});

export default function PricingGrid({ data }: PricingGridProps) {
  const styles = useStyles();

  const sortedData = useMemo(
    () => [...data].sort((a, b) => Math.abs(b.changePercent) - Math.abs(a.changePercent)),
    [data],
  );

  if (data.length === 0) {
    return (
      <div className={styles.wrapper}>
        <div className={styles.titleRow}>
          <span className={styles.title}>💰 Competitor Pricing Comparison</span>
        </div>
        <div className={styles.empty} data-testid="pricing-empty">No pricing data available</div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper} data-testid="pricing-grid">
      <div className={styles.titleRow}>
        <span className={styles.title}>💰 Competitor Pricing Comparison</span>
        <Badge appearance="filled" style={{ background: 'rgba(239,68,68,0.15)', color: '#fca5a5' }}>
          {data.length} SKUs tracked
        </Badge>
      </div>

      <table className={styles.table}>
        <thead>
          <tr>
            <th className={styles.th}>Competitor</th>
            <th className={styles.th}>SKU / Category</th>
            <th className={styles.th}>Current</th>
            <th className={styles.th}>Previous</th>
            <th className={styles.th}>Change</th>
            <th className={styles.th} style={{ width: '160px' }}>6-Month Trend</th>
          </tr>
        </thead>
        <tbody>
          {sortedData.map((row, idx) => {
            const isDramatic = Math.abs(row.changePercent) > 10;
            const isDown = row.changePercent < 0;
            const changeColor = isDown ? COMPETITIVE_COLORS.priceUp : COMPETITIVE_COLORS.priceDown;
            const arrow = isDown ? '↓' : row.changePercent > 0 ? '↑' : '—';

            return (
              <tr
                key={`${row.competitor}-${row.sku}-${idx}`}
                className={`${styles.row} ${isDramatic ? styles.alertRow : ''}`}
                data-testid="pricing-row"
              >
                <td className={styles.td} style={{ fontWeight: 600 }}>{row.competitor}</td>
                <td className={styles.td}>
                  {row.sku}
                  <span style={{ color: '#94a3b8', marginLeft: 8, fontSize: 11 }}>{row.category}</span>
                </td>
                <td className={styles.td}>${row.currentPrice.toFixed(2)}</td>
                <td className={styles.td} style={{ color: '#94a3b8' }}>${row.previousPrice.toFixed(2)}</td>
                <td className={styles.td}>
                  <span style={{ color: changeColor, fontWeight: 700 }}>
                    {arrow} {Math.abs(row.changePercent).toFixed(1)}%
                  </span>
                  {isDramatic && (
                    <Badge
                      appearance="filled"
                      style={{ marginLeft: 6, background: 'rgba(239,68,68,0.2)', color: '#fca5a5', fontSize: 10 }}
                    >
                      ⚠️ ALERT
                    </Badge>
                  )}
                </td>
                <td className={styles.td}>
                  <div className={styles.sparkline}>
                    {row.priceHistory.length > 0 && (
                      <ResponsiveContainer width="100%" height={30}>
                        <LineChart data={row.priceHistory} margin={{ top: 2, right: 4, bottom: 2, left: 4 }}>
                          <Line
                            type="monotone"
                            dataKey="price"
                            stroke={changeColor}
                            strokeWidth={1.5}
                            dot={false}
                            isAnimationActive={false}
                          />
                        </LineChart>
                      </ResponsiveContainer>
                    )}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
