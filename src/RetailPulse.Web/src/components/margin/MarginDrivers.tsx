import { useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { MARGIN_COLORS } from '../../constants/agentRouting';
import type { MarginDriver } from '../../types';

interface MarginDriversProps {
  drivers: MarginDriver[];
}

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    backgroundColor: MARGIN_COLORS.cardBg,
    border: `1px solid ${MARGIN_COLORS.cardBorder}`,
    borderRadius: '12px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#6366f1',
    marginBottom: '16px',
  },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '10px 0',
    borderBottom: '1px solid rgba(255,255,255,0.05)',
    '&:last-child': { borderBottom: 'none' },
  },
  name: {
    // API-sourced driver categories are longer than the demo strings this was
    // sized for, so they collided with the bars. Widen and keep the ellipsis.
    width: '180px',
    flexShrink: 0,
    fontSize: '13px',
    color: '#e2e8f0',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  barContainer: {
    flex: 1,
    height: '24px',
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
  },
  barTrack: {
    position: 'absolute',
    top: '50%',
    left: 0,
    right: 0,
    height: '2px',
    transform: 'translateY(-50%)',
    backgroundColor: 'rgba(255,255,255,0.06)',
  },
  bar: {
    position: 'absolute',
    height: '20px',
    borderRadius: '4px',
    transitionProperty: 'width',
    transitionDuration: '600ms',
    transitionTimingFunction: 'ease-out',
    top: '2px',
  },
  impactLabel: {
    width: '78px',
    textAlign: 'right',
    fontSize: '13px',
    fontWeight: '600',
    flexShrink: 0,
    fontVariantNumeric: 'tabular-nums',
  },
  trendArrow: {
    width: '24px',
    textAlign: 'center',
    fontSize: '14px',
    flexShrink: 0,
  },
  riskBadge: {
    fontSize: '11px',
    padding: '2px 6px',
    borderRadius: '4px',
    backgroundColor: 'rgba(239,68,68,0.15)',
    color: MARGIN_COLORS.negativeImpact,
    fontWeight: '600',
    flexShrink: 0,
  },
  empty: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '200px',
    color: '#94a3b8',
    fontSize: '14px',
  },
});

const TREND_MAP = {
  improving: { arrow: '↑', color: MARGIN_COLORS.improving },
  worsening: { arrow: '↓', color: MARGIN_COLORS.worsening },
  stable: { arrow: '→', color: MARGIN_COLORS.stable },
} as const;

export function MarginDrivers({ drivers }: MarginDriversProps) {
  const styles = useStyles();

  const sorted = useMemo(
    () => [...drivers].sort((a, b) => Math.abs(b.impact) - Math.abs(a.impact)),
    [drivers],
  );

  const maxAbsImpact = useMemo(
    () => sorted.reduce((m, d) => Math.max(m, Math.abs(d.impact)), 0) || 1,
    [sorted],
  );

  if (!drivers.length) {
    return (
      <div className={styles.wrapper}>
        <div className={styles.title}>Margin Drivers</div>
        <div className={styles.empty}>No driver data available</div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper} data-testid="margin-drivers">
      <div className={styles.title}>Margin Drivers</div>
      {sorted.map((d) => {
        const isPositive = d.impact >= 0;
        const widthPct = (Math.abs(d.impact) / maxAbsImpact) * 100;
        const barColor = isPositive ? MARGIN_COLORS.positiveImpact : MARGIN_COLORS.negativeImpact;
        const trend = TREND_MAP[d.trend];

        return (
          <div key={d.name} className={styles.row}>
            <span className={styles.name} title={d.name}>{d.name}</span>
            <div className={styles.barContainer}>
              <div className={styles.barTrack} />
              <div
                className={styles.bar}
                style={{
                  width: `${widthPct}%`,
                  backgroundColor: barColor,
                  left: isPositive ? '50%' : undefined,
                  right: isPositive ? undefined : '50%',
                  opacity: 0.85,
                }}
              />
            </div>
            <span
              className={styles.impactLabel}
              style={{ color: barColor }}
            >
              {isPositive ? '+' : ''}{d.impact.toFixed(1)}%
            </span>
            <span className={styles.trendArrow} style={{ color: trend.color }}>
              {trend.arrow}
            </span>
            {d.isRisk && <span className={styles.riskBadge}>⚠️ Risk</span>}
          </div>
        );
      })}
    </div>
  );
}
