import { makeStyles } from '@fluentui/react-components';
import type { KnowledgeAgentBinding, KnowledgeNamedSource } from '../../types';

interface AgentBindingsPanelProps {
  bindings: KnowledgeAgentBinding[];
  sources: KnowledgeNamedSource[];
}

const useStyles = makeStyles({
  wrapper: {
    padding: '16px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface, rgba(255,255,255,0.03))',
    border: '1px solid var(--color-border, rgba(255,255,255,0.08))',
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  title: {
    fontSize: '13px',
    fontWeight: '700',
    color: 'var(--color-text, #ffffff)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '10px',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  sectionLabel: {
    fontSize: '11px',
    textTransform: 'uppercase',
    letterSpacing: '0.6px',
    color: 'var(--color-text-muted, #94a3b8)',
    marginTop: '4px',
  },
  sourceRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '8px 10px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.04))',
    border: '1px solid var(--color-border, rgba(255,255,255,0.06))',
  },
  sourceName: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text, #ffffff)',
  },
  sourceDocs: {
    fontSize: '11px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  bindingRow: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '10px',
    padding: '8px 10px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.04))',
    border: '1px solid var(--color-border, rgba(255,255,255,0.06))',
  },
  agentName: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text, #ffffff)',
  },
  agentScope: {
    fontSize: '11px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  statusBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '10px',
    fontWeight: '700',
    letterSpacing: '0.4px',
    padding: '2px 8px',
    borderRadius: '999px',
    border: '1px solid var(--color-border, rgba(255,255,255,0.15))',
    textTransform: 'uppercase',
  },
  statusEnabled: {
    color: 'var(--color-accent-success, #22c55e)',
    borderColor: 'var(--color-accent-success, #22c55e)',
    backgroundColor: 'rgba(34,197,94,0.08)',
  },
  statusDisabled: {
    color: 'var(--color-text-muted, #94a3b8)',
    borderColor: 'var(--color-border, rgba(255,255,255,0.2))',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.04))',
  },
  empty: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    padding: '4px 0',
  },
});

function describeScope(binding: KnowledgeAgentBinding): string {
  if (!binding.enabled) return 'Knowledge disabled — no retrieval will run';
  if (binding.sourceName && binding.sourceName.trim().length > 0) {
    const docs = binding.sources.length > 0
      ? ` (${binding.sources.join(', ')})`
      : '';
    return `Bound to “${binding.sourceName}”${docs}`;
  }
  return 'Unscoped — sees the entire corpus';
}

export default function AgentBindingsPanel({ bindings, sources }: AgentBindingsPanelProps) {
  const styles = useStyles();
  const sortedBindings = [...bindings].sort((a, b) => a.agentDisplayName.localeCompare(b.agentDisplayName));
  const sortedSources = [...sources].sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className={styles.wrapper} data-testid="kb-agent-bindings">
      <div className={styles.title}>
        <span>🔗 Per-Agent Knowledge Bindings</span>
        <span
          className={styles.agentScope}
          data-testid="kb-agent-bindings-count"
        >
          {bindings.length} {bindings.length === 1 ? 'agent' : 'agents'}
        </span>
      </div>

      <div className={styles.sectionLabel}>Named sources</div>
      <div className={styles.section} data-testid="kb-named-sources">
        {sortedSources.length === 0 ? (
          <div className={styles.empty} data-testid="kb-named-sources-empty">
            No named sources configured — every enabled agent uses the full corpus.
          </div>
        ) : (
          sortedSources.map(source => (
            <div
              key={source.name}
              className={styles.sourceRow}
              data-testid={`kb-named-source-${source.name}`}
            >
              <span className={styles.sourceName}>{source.name}</span>
              <span className={styles.sourceDocs}>
                {source.documents.length > 0
                  ? source.documents.join(', ')
                  : 'No documents assigned'}
              </span>
            </div>
          ))
        )}
      </div>

      <div className={styles.sectionLabel}>Agents</div>
      <div className={styles.section}>
        {sortedBindings.length === 0 ? (
          <div className={styles.empty} data-testid="kb-bindings-empty">
            No specialist agents configured.
          </div>
        ) : (
          sortedBindings.map(binding => (
            <div
              key={binding.agentKey}
              className={styles.bindingRow}
              data-testid={`kb-binding-${binding.agentKey}`}
              data-binding-enabled={binding.enabled ? 'true' : 'false'}
            >
              <div>
                <div className={styles.agentName}>{binding.agentDisplayName}</div>
                <div className={styles.agentScope}>{describeScope(binding)}</div>
              </div>
              <span
                className={`${styles.statusBadge} ${binding.enabled ? styles.statusEnabled : styles.statusDisabled}`}
                aria-label={binding.enabled ? 'Retrieval enabled' : 'Retrieval disabled'}
              >
                {binding.enabled ? 'Enabled' : 'Disabled'}
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
