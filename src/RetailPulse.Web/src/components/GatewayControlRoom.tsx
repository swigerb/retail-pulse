import { makeStyles, mergeClasses } from '@fluentui/react-components';
import type { GatewayEvidence } from '../types';
import { buildGatewayRibbon } from '../utils/gatewayRibbon';

const useStyles = makeStyles({
  root: {
    marginTop: '6px',
    alignSelf: 'stretch',
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '4px 8px',
    padding: '6px 10px',
    borderRadius: '8px',
    fontSize: '11px',
    border: '1px solid var(--colorNeutralStroke2, rgba(255,255,255,0.12))',
    backgroundColor: 'var(--colorNeutralBackground3, rgba(255,255,255,0.04))',
  },
  status: {
    fontWeight: 600,
    padding: '1px 8px',
    borderRadius: '10px',
    whiteSpace: 'nowrap',
  },
  list: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px 12px',
    margin: 0,
    padding: 0,
    listStyleType: 'none',
    '@media (max-width: 600px)': {
      flexDirection: 'column',
      width: '100%',
    },
  },
  item: {
    display: 'inline-flex',
    gap: '4px',
    minWidth: 0,
    maxWidth: '100%',
  },
  label: { opacity: 0.7 },
  value: { fontWeight: 500, overflowWrap: 'anywhere' },
  note: { width: '100%', opacity: 0.8 },
  observed: { backgroundColor: 'rgba(34,197,94,0.15)', color: '#4ade80' },
  neutral: { backgroundColor: 'rgba(148,163,184,0.18)', color: '#cbd5e1' },
  warn: { backgroundColor: 'rgba(250,204,21,0.15)', color: '#facc15' },
  danger: { backgroundColor: 'rgba(248,113,113,0.15)', color: '#f87171' },
  itemBlocked: { color: '#f87171', fontWeight: 700 },
  itemWarning: { color: '#facc15' },
  quota: {
    width: '100%',
    padding: '6px 8px',
    borderRadius: '6px',
    backgroundColor: 'rgba(250,204,21,0.10)',
    border: '1px solid rgba(250,204,21,0.35)',
  },
  quotaTitle: { fontWeight: 700, color: '#facc15' },
  quotaSteps: { margin: '4px 0 0', paddingLeft: '18px' },
  dim: { opacity: 0.6, fontStyle: 'italic' },
});

export function GatewayControlRoom({ gateway }: { gateway: GatewayEvidence | null | undefined }) {
  const styles = useStyles();
  const model = buildGatewayRibbon(gateway);
  const tone =
    model.state === 'observed' ? styles.observed
    : model.state === 'blocked' || model.state === 'failed' ? styles.danger
    : model.state === 'throttled' || model.state === 'truncated' || model.state === 'warning' ? styles.warn
    : styles.neutral;

  return (
    <div role="group"
      className={styles.root}
      data-testid="gateway-control-room"
      data-gateway-state={model.state}
      aria-label={`Gateway Control Room: ${model.statusLabel}`}
    >
      <span className={mergeClasses(styles.status, tone)} data-testid="gateway-status">
        {model.statusLabel}
      </span>
      <ul className={styles.list} aria-label="Gateway evidence">
        {model.items.map(item => (
          <li
            key={item.key}
            className={styles.item}
            data-testid={`gateway-item-${item.key}`}
            data-item-state={item.state}
            aria-label={item.key === 'gatewaySafety' ? `${item.label}: ${item.value}` : undefined}
          >
            <span className={styles.label}>{item.label}:</span>
            <span
              className={mergeClasses(
                styles.value,
                (item.state === 'unavailable' || item.state === 'unknown') && styles.dim,
                item.state === 'blocked' && styles.itemBlocked,
                item.state === 'warning' && styles.itemWarning,
              )}
            >
              {item.value}
            </span>
          </li>
        ))}
      </ul>
      {model.quota && (
        <div role="status" className={styles.quota} data-testid="gateway-quota-state">
          <div className={styles.quotaTitle}>
            Token budget exhausted — {model.quota.scopeLabel} scope · {model.quota.modeLabel}
          </div>
          <div data-testid="gateway-quota-numbers">
            Retry after: {model.quota.retryAfterSeconds != null ? `${model.quota.retryAfterSeconds}s` : 'not reported'}
            {' · '}Remaining: {model.quota.remaining != null ? model.quota.remaining.toLocaleString() : 'not reported'}
          </div>
          <ol className={styles.quotaSteps} aria-label="Quota reset walkthrough">
            {model.quota.steps.map(step => <li key={step}>{step}</li>)}
          </ol>
        </div>
      )}
      {model.truncatedNote && (
        <span className={styles.note} role="note" data-testid="gateway-truncated">
          {model.truncatedNote}
        </span>
      )}
    </div>
  );
}
