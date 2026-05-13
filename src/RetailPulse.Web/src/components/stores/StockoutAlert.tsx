import { useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { STORE_COLORS } from '../../constants/agentRouting';
import type { StockoutRisk } from '../../types';

interface StockoutAlertProps {
  risks: StockoutRisk[];
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
    color: STORE_COLORS.stockoutUrgent,
  },
  countBadge: {
    fontSize: '11px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    background: 'rgba(239,68,68,0.15)',
    color: '#fca5a5',
  },
  cards: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '10px',
  },
  card: {
    flex: '1 1 280px',
    maxWidth: '420px',
    borderRadius: '10px',
    padding: '14px 16px',
    border: '1px solid',
    transition: 'all 0.2s ease',
    ':hover': {
      transform: 'translateY(-1px)',
      boxShadow: '0 4px 16px rgba(0,0,0,0.3)',
    },
  },
  cardUrgent: {
    background: STORE_COLORS.redBg,
    borderColor: STORE_COLORS.stockoutUrgent as unknown as undefined,
  },
  cardWarning: {
    background: STORE_COLORS.yellowBg,
    borderColor: STORE_COLORS.stockoutWarning as unknown as undefined,
  },
  cardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '10px',
    flexWrap: 'wrap',
  },
  skuName: {
    fontSize: '14px',
    fontWeight: '700',
    color: 'var(--color-text, #e2e8f0)',
    flex: 1,
  },
  urgentBadge: {
    fontSize: '10px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    background: STORE_COLORS.stockoutUrgent,
    color: '#fff',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    animation: 'pulse 2s ease-in-out infinite',
  },
  meta: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '6px 12px',
    fontSize: '12px',
    color: 'var(--color-text-muted, rgba(255,255,255,0.6))',
  },
  metaLabel: {
    fontWeight: '500',
    color: 'var(--color-text-muted, rgba(255,255,255,0.45))',
  },
  metaValue: {
    fontWeight: '600',
    color: 'var(--color-text, #e2e8f0)',
  },
  daysValue: {
    fontWeight: '800',
    fontSize: '16px',
  },
  reorderRow: {
    marginTop: '10px',
    paddingTop: '8px',
    borderTop: '1px solid rgba(255,255,255,0.08)',
    fontSize: '12px',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  reorderLabel: {
    color: 'var(--color-text-muted, rgba(255,255,255,0.45))',
  },
  reorderQty: {
    fontWeight: '700',
    color: STORE_COLORS.green,
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted, rgba(255,255,255,0.5))',
    fontSize: '14px',
  },
});

export function StockoutAlert({ risks }: StockoutAlertProps) {
  const styles = useStyles();

  const sorted = useMemo(
    () => [...risks].sort((a, b) => a.daysRemaining - b.daysRemaining),
    [risks],
  );

  if (risks.length === 0) {
    return (
      <div data-testid="stockout-alert">
        <div className={styles.titleRow}>
          <span className={styles.title}>⚠️ Stockout Risks</span>
        </div>
        <div className={styles.empty} data-testid="stockout-empty">No stockout risks detected</div>
      </div>
    );
  }

  return (
    <div data-testid="stockout-alert">
      <div className={styles.titleRow}>
        <span className={styles.title}>⚠️ Stockout Risks</span>
        <span className={styles.countBadge}>{risks.length} at risk</span>
      </div>
      <div className={styles.cards}>
        {sorted.map(risk => {
          const isUrgent = risk.daysRemaining < 3;
          return (
            <div
              key={risk.skuId}
              className={`${styles.card} ${isUrgent ? styles.cardUrgent : styles.cardWarning}`}
              data-testid="stockout-card"
              role="article"
              aria-label={`${risk.skuName} - ${risk.daysRemaining} days remaining`}
            >
              <div className={styles.cardHeader}>
                <span className={styles.skuName}>{risk.skuName}</span>
                {isUrgent && (
                  <span className={styles.urgentBadge} data-testid="urgent-badge">
                    🚨 Urgent
                  </span>
                )}
              </div>
              <div className={styles.meta}>
                <span className={styles.metaLabel}>Brand</span>
                <span className={styles.metaValue}>{risk.brand}</span>

                <span className={styles.metaLabel}>Region</span>
                <span className={styles.metaValue}>{risk.region}</span>

                <span className={styles.metaLabel}>Velocity</span>
                <span className={styles.metaValue}>{risk.currentVelocity.toFixed(1)} units/day</span>

                <span className={styles.metaLabel}>Days Left</span>
                <span
                  className={styles.daysValue}
                  style={{ color: isUrgent ? STORE_COLORS.stockoutUrgent : STORE_COLORS.stockoutWarning }}
                >
                  {risk.daysRemaining}
                </span>
              </div>
              <div className={styles.reorderRow}>
                <span className={styles.reorderLabel}>📦 Recommended reorder:</span>
                <span className={styles.reorderQty}>{risk.recommendedReorder} units</span>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
