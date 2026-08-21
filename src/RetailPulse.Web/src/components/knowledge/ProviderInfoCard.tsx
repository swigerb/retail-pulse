import { makeStyles } from '@fluentui/react-components';
import type {
  KnowledgeDegradationInfo,
  KnowledgeProviderInfo,
  KnowledgeQuotas,
  KnowledgeUsage,
} from '../../types';

interface ProviderInfoCardProps {
  provider: KnowledgeProviderInfo;
  degradation: KnowledgeDegradationInfo;
  quotas: KnowledgeQuotas;
  usage: KnowledgeUsage;
}

const useStyles = makeStyles({
  card: {
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface, rgba(255,255,255,0.03))',
    border: '1px solid var(--color-border, rgba(255,255,255,0.08))',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    flexWrap: 'wrap',
  },
  providerName: {
    fontSize: '15px',
    fontWeight: '700',
    color: 'var(--color-text, #ffffff)',
  },
  eyebrow: {
    fontSize: '11px',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  badgeRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
  },
  badge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 10px',
    borderRadius: '999px',
    fontSize: '11px',
    fontWeight: '600',
    border: '1px solid var(--color-border, rgba(255,255,255,0.12))',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.05))',
    color: 'var(--color-text, #ffffff)',
  },
  badgeDurable: {
    borderColor: 'var(--color-accent-success, #22c55e)',
    color: 'var(--color-accent-success, #22c55e)',
    backgroundColor: 'rgba(34,197,94,0.10)',
  },
  badgeVolatile: {
    borderColor: 'var(--color-accent-warning, #f59e0b)',
    color: 'var(--color-accent-warning, #f59e0b)',
    backgroundColor: 'rgba(245,158,11,0.12)',
  },
  scoreNote: {
    fontSize: '12px',
    lineHeight: '1.55',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  degradation: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
  },
  fallbackAlert: {
    fontSize: '12px',
    padding: '8px 12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(245,158,11,0.12)',
    border: '1px solid var(--color-accent-warning, #f59e0b)',
    color: 'var(--color-accent-warning, #f59e0b)',
  },
  quotaGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  quotaRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  quotaLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: '12px',
    color: 'var(--color-text, #ffffff)',
  },
  quotaLabelMuted: {
    color: 'var(--color-text-muted, #94a3b8)',
    fontSize: '11px',
  },
  quotaBarTrack: {
    height: '6px',
    borderRadius: '3px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.06))',
    overflow: 'hidden',
    border: '1px solid var(--color-border, rgba(255,255,255,0.06))',
  },
  quotaBarFill: {
    height: '100%',
    borderRadius: '3px',
    backgroundColor: 'var(--color-accent-success, #22c55e)',
    transition: 'width 0.25s ease',
  },
  quotaBarFillWarn: {
    backgroundColor: 'var(--color-accent-warning, #f59e0b)',
  },
  quotaBarFillDanger: {
    backgroundColor: 'var(--color-accent-danger, #ef4444)',
  },
});

function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${Math.round(bytes / (1024 * 1024))} MB`;
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${bytes} B`;
}

function relevanceLabel(kind: KnowledgeProviderInfo['relevance']): string {
  switch (kind) {
    case 'Semantic': return 'Semantic (vector similarity)';
    case 'Hybrid': return 'Hybrid (semantic + lexical)';
    case 'Lexical':
    default: return 'Lexical (keyword / BM25)';
  }
}

function QuotaBar({
  label,
  used,
  max,
  testId,
}: { label: string; used: number; max: number; testId: string }) {
  const styles = useStyles();
  const pctRaw = max > 0 ? (used / max) * 100 : 0;
  const pct = Math.max(0, Math.min(100, pctRaw));
  const exceeded = used >= max && max > 0;
  const warn = pct >= 90 || exceeded;
  const fillClass = [
    styles.quotaBarFill,
    exceeded ? styles.quotaBarFillDanger : (warn ? styles.quotaBarFillWarn : ''),
  ].filter(Boolean).join(' ');
  const status = exceeded ? 'exceeded' : (warn ? 'warn' : 'ok');
  return (
    <div className={styles.quotaRow} data-testid={testId} data-quota-status={status}>
      <div className={styles.quotaLabel}>
        <span>{label}</span>
        <span className={styles.quotaLabelMuted}>{used.toLocaleString()} / {max.toLocaleString()}</span>
      </div>
      <div
        className={styles.quotaBarTrack}
        role="progressbar"
        aria-label={`${label} usage`}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuenow={used}
      >
        <div className={fillClass} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

export default function ProviderInfoCard({ provider, degradation, quotas, usage }: ProviderInfoCardProps) {
  const styles = useStyles();
  const durabilityClass = provider.persistent ? styles.badgeDurable : styles.badgeVolatile;
  const durabilityLabel = provider.persistent ? 'Durable' : 'Volatile';
  const durabilityIcon = provider.persistent ? '🛡️' : '⚠️';
  const locality = provider.requiresCloud ? 'Cloud-hosted' : 'Local process';

  return (
    <div className={styles.card} data-testid="kb-provider-info">
      <div>
        <div className={styles.eyebrow}>Active retrieval provider</div>
        <div className={styles.header}>
          <span className={styles.providerName} data-testid="kb-provider-name">{provider.name}</span>
          <span
            className={`${styles.badge} ${durabilityClass}`}
            data-testid="kb-provider-durability"
            data-durability={provider.persistent ? 'durable' : 'volatile'}
            aria-label={`Durability: ${durabilityLabel}`}
          >
            <span aria-hidden="true">{durabilityIcon}</span>
            {durabilityLabel}
          </span>
          <span className={styles.badge} data-testid="kb-provider-relevance">
            {relevanceLabel(provider.relevance)}
          </span>
          <span className={styles.badge} data-testid="kb-provider-locality">
            {locality}
          </span>
          <span
            className={styles.badge}
            data-testid="kb-provider-mutation"
            data-mutation={provider.supportsMutation ? 'supported' : 'read-only'}
          >
            {provider.supportsMutation ? 'Ingestion enabled' : 'Read-only corpus'}
          </span>
        </div>
      </div>

      <div className={styles.scoreNote} data-testid="kb-score-semantics">
        {provider.scoreSemantics}
      </div>

      <div className={styles.degradation} data-testid="kb-degradation">
        <span>Degradation policy: {degradation.mode ?? 'unknown'}</span>
      </div>

      {degradation.primaryReplacedByFallback && (
        <div className={styles.fallbackAlert} role="alert" data-testid="kb-fallback-alert">
          ⚠️ The configured provider was unreachable at startup. Requests are being
          served by the in-memory fallback until it recovers.
        </div>
      )}

      <div className={styles.quotaGroup}>
        <QuotaBar
          label="Documents"
          used={usage.documentCount}
          max={quotas.maxDocuments}
          testId="kb-quota-documents"
        />
        <QuotaBar
          label="Chunks"
          used={usage.chunkCount}
          max={quotas.maxChunks}
          testId="kb-quota-chunks"
        />
        <div className={styles.quotaLabel}>
          <span>Max document size</span>
          <span className={styles.quotaLabelMuted} data-testid="kb-quota-doc-size">
            {formatBytes(quotas.maxDocumentSizeBytes)}
          </span>
        </div>
      </div>
    </div>
  );
}
