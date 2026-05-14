import {
  Dialog,
  DialogSurface,
  DialogBody,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  Badge,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Dismiss24Regular } from '@fluentui/react-icons';
import { STORE_COLORS } from '../../constants/agentRouting';
import type { StorePerformance, PerformanceLevel } from '../../types';

interface StoreDetailDialogProps {
  store: StorePerformance | null;
  open: boolean;
  onClose: () => void;
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

const LEVEL_LABEL: Record<PerformanceLevel, string> = {
  green: 'On Target',
  yellow: 'Near Target',
  red: 'Below Target',
};

const LEVEL_COLOR: Record<PerformanceLevel, string> = {
  green: STORE_COLORS.green,
  yellow: STORE_COLORS.yellow,
  red: STORE_COLORS.red,
};

const useStyles = makeStyles({
  surface: {
    maxWidth: '480px',
    background: 'var(--color-bg-secondary, #1e293b)',
    border: `1px solid ${STORE_COLORS.cardBorder}`,
  },
  metricGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '12px',
    marginBottom: '16px',
  },
  metricCard: {
    borderRadius: '8px',
    padding: '12px',
    background: 'var(--color-bg-tertiary, rgba(255,255,255,0.04))',
    border: `1px solid ${STORE_COLORS.cardBorder}`,
  },
  metricLabel: {
    fontSize: '11px',
    fontWeight: '500',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  metricValue: {
    fontSize: '18px',
    fontWeight: '700',
    marginTop: '4px',
  },
  sectionTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: tokens.colorNeutralForeground3,
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '8px',
    marginTop: '12px',
  },
  issueList: {
    listStyleType: 'none',
    padding: '0',
    margin: '0',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  issueItem: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground1,
    padding: '6px 10px',
    borderRadius: '6px',
    background: 'rgba(239, 68, 68, 0.08)',
    borderLeft: '3px solid ' + STORE_COLORS.red,
  },
  recItem: {
    fontSize: '13px',
    color: tokens.colorNeutralForeground1,
    padding: '6px 10px',
    borderRadius: '6px',
    background: 'rgba(34, 197, 94, 0.08)',
    borderLeft: '3px solid ' + STORE_COLORS.green,
  },
  perfBadge: {
    display: 'inline-block',
    padding: '2px 10px',
    borderRadius: '4px',
    fontWeight: '700',
    fontSize: '12px',
  },
});

export function StoreDetailDialog({ store, open, onClose }: StoreDetailDialogProps) {
  const styles = useStyles();

  if (!store) return null;

  const level = getPerformanceLevel(store);

  return (
    <Dialog open={open} onOpenChange={(_, data) => { if (!data.open) onClose(); }}>
      <DialogSurface className={styles.surface} data-testid="store-detail-dialog">
        <DialogBody>
          <DialogTitle
            action={
              <Button
                appearance="subtle"
                aria-label="Close"
                icon={<Dismiss24Regular />}
                onClick={onClose}
              />
            }
          >
            {store.storeName}
          </DialogTitle>
          <DialogContent>
            <div style={{ marginBottom: '8px' }}>
              <Badge appearance="tint" color="informative" size="small">{store.region}</Badge>
              {' '}
              <span className={styles.perfBadge} style={{ background: `${LEVEL_COLOR[level]}22`, color: LEVEL_COLOR[level] }}>
                {LEVEL_LABEL[level]} — {store.performanceIndex}%
              </span>
            </div>

            <div className={styles.metricGrid}>
              <div className={styles.metricCard}>
                <div className={styles.metricLabel}>Revenue</div>
                <div className={styles.metricValue} style={{ color: LEVEL_COLOR[level] }}>
                  {formatCurrency(store.revenue)}
                </div>
              </div>
              <div className={styles.metricCard}>
                <div className={styles.metricLabel}>Target</div>
                <div className={styles.metricValue} style={{ color: tokens.colorNeutralForeground1 }}>
                  {formatCurrency(store.target)}
                </div>
              </div>
            </div>

            {store.issues.length > 0 && (
              <>
                <div className={styles.sectionTitle}>⚠️ Issues ({store.issues.length})</div>
                <ul className={styles.issueList}>
                  {store.issues.map((issue, i) => (
                    <li key={i} className={styles.issueItem}>{issue}</li>
                  ))}
                </ul>
              </>
            )}

            {store.recommendations.length > 0 && (
              <>
                <div className={styles.sectionTitle}>💡 Recommendations</div>
                <ul className={styles.issueList}>
                  {store.recommendations.map((rec, i) => (
                    <li key={i} className={styles.recItem}>{rec}</li>
                  ))}
                </ul>
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
