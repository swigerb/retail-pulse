import { useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import type { ForecastData } from '../../types';

const useStyles = makeStyles({
  strip: {
    display: 'flex',
    gap: '12px',
    flexWrap: 'wrap',
    marginBottom: '16px',
  },
  card: {
    flex: '1 1 140px',
    minWidth: '140px',
    padding: '14px 16px',
    borderRadius: '10px',
    backgroundColor: 'rgba(255,255,255,0.04)',
    border: '1px solid rgba(255,255,255,0.08)',
  },
  label: {
    fontSize: '11px',
    fontWeight: '500',
    color: '#94a3b8',
    textTransform: 'uppercase' as const,
    letterSpacing: '0.5px',
    marginBottom: '4px',
  },
  value: {
    fontSize: '22px',
    fontWeight: '700',
    color: '#f1f5f9',
  },
  trendUp: {
    color: '#22c55e',
  },
  trendDown: {
    color: '#ef4444',
  },
  subtext: {
    fontSize: '12px',
    color: '#64748b',
    marginTop: '2px',
  },
});

export default function ForecastSummary({ data }: { data: ForecastData }) {
  const styles = useStyles();

  const metrics = useMemo(() => {
    const historicalAvg =
      data.historical.length > 0
        ? data.historical.reduce((s, h) => s + h.actual, 0) / data.historical.length
        : 0;
    const forecastAvg =
      data.predicted.length > 0
        ? data.predicted.reduce((s, p) => s + p.value, 0) / data.predicted.length
        : 0;
    const trendPct =
      historicalAvg > 0 ? ((forecastAvg - historicalAvg) / historicalAvg) * 100 : 0;
    const topSeason = data.seasonality.length > 0 ? data.seasonality[0] : null;

    return { historicalAvg, forecastAvg, trendPct, topSeason };
  }, [data]);

  const trendDir = metrics.trendPct >= 0 ? '↑' : '↓';
  const trendClass = metrics.trendPct >= 0 ? styles.trendUp : styles.trendDown;

  return (
    <div className={styles.strip} data-testid="forecast-summary">
      <div className={styles.card}>
        <div className={styles.label}>Current Avg</div>
        <div className={styles.value}>{Math.round(metrics.historicalAvg).toLocaleString()}</div>
        <div className={styles.subtext}>units/period</div>
      </div>
      <div className={styles.card}>
        <div className={styles.label}>Forecast Avg</div>
        <div className={styles.value}>{Math.round(metrics.forecastAvg).toLocaleString()}</div>
        <div className={styles.subtext}>units/period</div>
      </div>
      <div className={styles.card}>
        <div className={styles.label}>Trend</div>
        <div className={`${styles.value} ${trendClass}`}>
          {trendDir}{Math.abs(metrics.trendPct).toFixed(1)}%
        </div>
        <div className={styles.subtext}>vs current period</div>
      </div>
      <div className={styles.card}>
        <div className={styles.label}>Top Seasonal Factor</div>
        <div className={styles.value} style={{ fontSize: '16px' }}>
          {metrics.topSeason ? metrics.topSeason.factor : '—'}
        </div>
        <div className={styles.subtext}>
          {metrics.topSeason ? `${metrics.topSeason.impact} impact` : 'none upcoming'}
        </div>
      </div>
    </div>
  );
}
