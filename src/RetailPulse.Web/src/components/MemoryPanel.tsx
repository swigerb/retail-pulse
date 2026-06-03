import { useState, useEffect, useMemo, useCallback } from 'react';
import {
  Input,
  Button,
  Text,
  Spinner,
  Badge,
  makeStyles,
  Tooltip,
} from '@fluentui/react-components';
import { Delete24Regular, Search24Regular, DismissCircle24Regular } from '@fluentui/react-icons';
import type { MemoryEntry, MemoryType } from '../types';
import { fetchMemories, deleteMemory, deleteAllMemories } from '../services/memoryApi';

const MEMORY_TYPE_CONFIG: Record<MemoryType, { label: string; emoji: string; color: string }> = {
  conversation: { label: 'Conversations', emoji: '💬', color: '#3b82f6' },
  preference: { label: 'Preferences', emoji: '⚙️', color: '#22c55e' },
  entity: { label: 'Entities', emoji: '🏷️', color: '#f97316' },
};

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    backgroundColor: 'var(--color-surface)',
    borderRadius: '12px',
    border: '1px solid var(--color-border)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
  },
  title: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '16px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  searchRow: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
  },
  filterChips: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  filterChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '4px 12px',
    borderRadius: '16px',
    fontSize: '12px',
    fontWeight: '500',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    border: '1px solid var(--color-border)',
    backgroundColor: 'transparent',
    color: 'var(--color-text-muted)',
    ':hover': {
      backgroundColor: 'var(--color-surface-hover)',
    },
  },
  filterChipActive: {
    backgroundColor: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent-border)',
    color: 'var(--brand-accent)',
    fontWeight: '600',
  },
  groupTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '13px',
    fontWeight: '600',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginTop: '8px',
  },
  entryList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  entry: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '10px 12px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-bg)',
    border: '1px solid var(--color-border-faint)',
    transition: 'all 0.2s ease',
    ':hover': {
      backgroundColor: 'var(--color-surface-hover)',
      border: '1px solid var(--color-border)',
    },
  },
  entryContent: {
    flex: '1',
    minWidth: '0',
  },
  entryText: {
    fontSize: '13px',
    color: 'var(--color-text)',
    lineHeight: '1.4',
    wordBreak: 'break-word',
  },
  entryMeta: {
    display: 'flex',
    gap: '12px',
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
    marginTop: '4px',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '32px 16px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
  },
  emptyIcon: {
    fontSize: '32px',
    opacity: '0.5',
  },
  countBadge: {
    marginLeft: '4px',
  },
});

function formatRelativeTime(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  if (diffDays < 30) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

function formatExpiresIn(dateStr?: string): string | null {
  if (!dateStr) return null;
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = date.getTime() - now.getTime();
  if (diffMs <= 0) return 'expired';
  const diffHours = Math.floor(diffMs / 3600000);
  if (diffHours < 24) return `expires in ${diffHours}h`;
  const diffDays = Math.floor(diffHours / 24);
  return `expires in ${diffDays}d`;
}

export interface MemoryPanelProps {
  /** If provided, panel uses these entries instead of fetching */
  entries?: MemoryEntry[];
  /** Triggers a re-fetch when the panel manages its own entries */
  refreshKey?: number;
}

export function MemoryPanel({ entries: externalEntries, refreshKey }: MemoryPanelProps = {}) {
  const [entries, setEntries] = useState<MemoryEntry[]>(externalEntries ?? []);
  const [loading, setLoading] = useState(!externalEntries);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [typeFilter, setTypeFilter] = useState<MemoryType | null>(null);
  const styles = useStyles();

  useEffect(() => {
    if (externalEntries) {
      setEntries(externalEntries);
      return;
    }
    let cancelled = false;
    setLoading(true);
    fetchMemories()
      .then(data => { if (!cancelled) setEntries(data); })
      .catch(err => { if (!cancelled) setError(err.message); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [externalEntries, refreshKey]);

  const handleForget = useCallback(async (id: string) => {
    try {
      await deleteMemory(id);
      setEntries(prev => prev.filter(e => e.id !== id));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete');
    }
  }, []);

  const handleForgetAll = useCallback(async () => {
    try {
      await deleteAllMemories();
      setEntries([]);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete all');
    }
  }, []);

  const filtered = useMemo(() => {
    let result = entries;
    if (typeFilter) result = result.filter(e => e.type === typeFilter);
    if (search.trim()) {
      const q = search.toLowerCase();
      result = result.filter(e =>
        e.content.toLowerCase().includes(q) ||
        e.tags?.some(t => t.toLowerCase().includes(q))
      );
    }
    return result;
  }, [entries, typeFilter, search]);

  const grouped = useMemo(() => {
    const groups: Record<MemoryType, MemoryEntry[]> = {
      conversation: [],
      preference: [],
      entity: [],
    };
    for (const entry of filtered) {
      groups[entry.type]?.push(entry);
    }
    return groups;
  }, [filtered]);

  if (loading) {
    return (
      <div className={styles.panel} data-testid="memory-panel">
        <div className={styles.header}>
          <span className={styles.title}>🧠 Memory</span>
        </div>
        <Spinner size="small" label="Loading memories..." />
      </div>
    );
  }

  return (
    <div className={styles.panel} data-testid="memory-panel">
      <div className={styles.header}>
        <span className={styles.title}>
          🧠 Memory
          <Badge appearance="filled" color="informative" className={styles.countBadge}>
            {entries.length}
          </Badge>
        </span>
        {entries.length > 0 && (
          <Tooltip content="Forget all memories" relationship="label">
            <Button
              appearance="subtle"
              size="small"
              icon={<DismissCircle24Regular />}
              onClick={handleForgetAll}
              aria-label="Forget all memories"
              data-testid="forget-all-button"
            >
              Forget All
            </Button>
          </Tooltip>
        )}
      </div>

      {error && (
        <Text style={{ color: '#ef4444', fontSize: '12px' }}>{error}</Text>
      )}

      {entries.length > 0 && (
        <>
          <div className={styles.searchRow}>
            <Search24Regular style={{ color: 'var(--color-text-muted)', flexShrink: 0 }} />
            <Input
              value={search}
              onChange={(_e, data) => setSearch(data.value)}
              placeholder="Search memories..."
              size="small"
              style={{ flex: 1 }}
              data-testid="memory-search"
            />
          </div>

          <div className={styles.filterChips}>
            <button
              className={`${styles.filterChip} ${typeFilter === null ? styles.filterChipActive : ''}`}
              onClick={() => setTypeFilter(null)}
            >
              All ({entries.length})
            </button>
            {(Object.keys(MEMORY_TYPE_CONFIG) as MemoryType[]).map(type => {
              const count = entries.filter(e => e.type === type).length;
              if (count === 0) return null;
              const config = MEMORY_TYPE_CONFIG[type];
              return (
                <button
                  key={type}
                  className={`${styles.filterChip} ${typeFilter === type ? styles.filterChipActive : ''}`}
                  onClick={() => setTypeFilter(typeFilter === type ? null : type)}
                >
                  {config.emoji} {config.label} ({count})
                </button>
              );
            })}
          </div>
        </>
      )}

      {filtered.length === 0 ? (
        <div className={styles.emptyState} data-testid="memory-empty">
          <span className={styles.emptyIcon}>🧠</span>
          <Text>{entries.length === 0 ? 'No memories stored yet' : 'No matching memories'}</Text>
          <Text style={{ fontSize: '12px' }}>
            {entries.length === 0
              ? 'The agent will remember context from your conversations'
              : 'Try adjusting your search or filters'}
          </Text>
        </div>
      ) : (
        (Object.keys(MEMORY_TYPE_CONFIG) as MemoryType[]).map(type => {
          const group = grouped[type];
          if (group.length === 0) return null;
          const config = MEMORY_TYPE_CONFIG[type];
          return (
            <div key={type}>
              <div className={styles.groupTitle}>
                <span>{config.emoji}</span>
                <span>{config.label}</span>
                <Badge appearance="tint" size="small" color="informative">{group.length}</Badge>
              </div>
              <div className={styles.entryList}>
                {group.map(entry => {
                  const expires = formatExpiresIn(entry.expiresAt);
                  return (
                    <div key={entry.id} className={styles.entry} data-testid="memory-entry">
                      <div className={styles.entryContent}>
                        <div className={styles.entryText}>{entry.content}</div>
                        <div className={styles.entryMeta}>
                          <span>Stored {formatRelativeTime(entry.storedAt)}</span>
                          {expires && <span>{expires}</span>}
                          {entry.tags?.map(tag => (
                            <span key={tag} style={{ color: 'var(--brand-accent)' }}>#{tag}</span>
                          ))}
                        </div>
                      </div>
                      <Tooltip content="Forget this memory" relationship="label">
                        <Button
                          appearance="subtle"
                          size="small"
                          icon={<Delete24Regular />}
                          onClick={() => handleForget(entry.id)}
                          aria-label={`Forget: ${entry.content}`}
                          data-testid="forget-button"
                        />
                      </Tooltip>
                    </div>
                  );
                })}
              </div>
            </div>
          );
        })
      )}
    </div>
  );
}
