import { Badge, Button, Spinner, Text, makeStyles } from '@fluentui/react-components';
import { Delete20Regular, Open20Regular, ArrowClockwise20Regular } from '@fluentui/react-icons';
import type { PlanSummary } from '../../types';
import { PLAN_STATUS_META } from './statusMeta';

export interface PlanHistoryPanelProps {
  plans: PlanSummary[];
  loading?: boolean;
  error?: string;
  /**
   * True when the deployment exposes no plan surface at all
   * (`PlanPersistence:Enabled=false`, so `/api/plans/` is unmapped). Renders an
   * explanatory note instead of an alert — this is configuration, not failure.
   */
  unavailable?: boolean;
  activePlanId?: string | null;
  onRefresh: () => void;
  onOpen: (planId: string) => void;
  onDelete: (planId: string) => void;
}

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  header: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '8px',
  },
  title: {
    fontSize: '13px',
    fontWeight: 600,
    color: 'var(--color-text)',
  },
  empty: {
    fontSize: '12px',
    color: 'var(--color-text-subtle)',
    padding: '12px',
    textAlign: 'center',
    background: 'var(--color-surface)',
    borderRadius: '8px',
    border: '1px dashed var(--color-border)',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  row: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '10px 12px',
    borderRadius: '10px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    ':hover': {
      background: 'var(--color-surface-hover)',
    },
  },
  rowActive: {
    border: '1px solid var(--brand-accent-border)',
    background: 'var(--brand-accent-soft)',
  },
  topLine: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: '8px',
    alignItems: 'center',
  },
  request: {
    fontSize: '13px',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  meta: {
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
    display: 'flex',
    gap: '8px',
  },
  actions: {
    display: 'flex',
    gap: '4px',
  },
  error: {
    fontSize: '12px',
    color: 'var(--color-danger, var(--brand-primary))',
  },
  statusPill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '999px',
    fontSize: '11px',
    fontWeight: 600,
    letterSpacing: '0.4px',
    border: '1px solid var(--color-border)',
  },
});

function timeAgo(iso: string): string {
  const then = new Date(iso).getTime();
  if (!Number.isFinite(then)) return '';
  const diff = Date.now() - then;
  if (diff < 60_000) return 'just now';
  if (diff < 3_600_000) return `${Math.round(diff / 60_000)}m ago`;
  if (diff < 86_400_000) return `${Math.round(diff / 3_600_000)}h ago`;
  return `${Math.round(diff / 86_400_000)}d ago`;
}

export function PlanHistoryPanel({
  plans,
  loading,
  error,
  unavailable,
  activePlanId,
  onRefresh,
  onOpen,
  onDelete,
}: PlanHistoryPanelProps) {
  const styles = useStyles();

  return (
    <div className={styles.panel} data-testid="plan-history">
      <div className={styles.header}>
        <Text className={styles.title}>Recent plans</Text>
        <Button
          appearance="subtle"
          icon={loading ? <Spinner size="tiny" /> : <ArrowClockwise20Regular />}
          onClick={onRefresh}
          disabled={loading || unavailable}
          aria-label="Refresh plan history"
          data-testid="plan-history-refresh"
        >
          Refresh
        </Button>
      </div>
      {error && !unavailable && <Text className={styles.error} role="alert">{error}</Text>}
      {unavailable ? (
        <div className={styles.empty} data-testid="plan-history-unavailable">
          Plan history isn&apos;t enabled in this environment.
        </div>
      ) : !loading && plans.length === 0 ? (
        <div className={styles.empty} data-testid="plan-history-empty">
          No plans yet. Ask a multi-domain question to generate one.
        </div>
      ) : (
        <div className={styles.list}>
          {plans.map(plan => {
            const meta = PLAN_STATUS_META[plan.status] ?? PLAN_STATUS_META.completed;
            const isActive = plan.planId === activePlanId;
            return (
              <div
                key={plan.planId}
                className={`${styles.row} ${isActive ? styles.rowActive : ''}`}
                data-testid="plan-history-row"
                data-plan-id={plan.planId}
              >
                <div className={styles.topLine}>
                  <span
                    className={styles.statusPill}
                    style={{ color: meta.fg, background: meta.bg, borderColor: meta.border }}
                  >
                    <span aria-hidden="true">{meta.icon}</span>
                    {meta.label}
                  </span>
                  <Badge appearance="tint" size="small">{plan.stepCount} steps</Badge>
                </div>
                <Text className={styles.request} title={plan.request}>{plan.request}</Text>
                <div className={styles.topLine}>
                  <span className={styles.meta}>
                    <span>{timeAgo(plan.updatedAt)}</span>
                  </span>
                  <div className={styles.actions}>
                    <Button
                      appearance="subtle"
                      icon={<Open20Regular />}
                      onClick={() => onOpen(plan.planId)}
                      aria-label={`Reopen plan ${plan.planId}`}
                      data-testid="plan-history-open"
                    >
                      Open
                    </Button>
                    <Button
                      appearance="subtle"
                      icon={<Delete20Regular />}
                      onClick={() => onDelete(plan.planId)}
                      aria-label={`Delete plan ${plan.planId}`}
                      data-testid="plan-history-delete"
                    />
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
