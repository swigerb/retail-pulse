import { useState } from 'react';
import { Tooltip, makeStyles } from '@fluentui/react-components';
import type { MemoryContext } from '../types';

export interface MemoryIndicatorProps {
  memoryContext: MemoryContext;
}

const useStyles = makeStyles({
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '3px 10px',
    borderRadius: '16px',
    fontSize: '11px',
    fontWeight: '500',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    backgroundColor: 'rgba(139, 92, 246, 0.12)',
    color: '#c4b5fd',
    border: '1px solid rgba(139, 92, 246, 0.25)',
    marginTop: '4px',
    ':hover': {
      backgroundColor: 'rgba(139, 92, 246, 0.2)',
      border: '1px solid rgba(139, 92, 246, 0.4)',
    },
  },
  icon: {
    fontSize: '12px',
    lineHeight: '1',
  },
  label: {
    maxWidth: '280px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  tooltipContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    maxWidth: '320px',
    fontSize: '12px',
  },
  tooltipHeader: {
    fontWeight: '600',
    color: '#c4b5fd',
    borderBottom: '1px solid rgba(139, 92, 246, 0.3)',
    paddingBottom: '4px',
    marginBottom: '2px',
  },
  tooltipEntry: {
    display: 'flex',
    gap: '6px',
    alignItems: 'flex-start',
    padding: '2px 0',
  },
  tooltipType: {
    fontSize: '10px',
    textTransform: 'uppercase',
    color: '#a78bfa',
    fontWeight: '600',
    flexShrink: '0',
    minWidth: '60px',
  },
  tooltipText: {
    color: 'var(--color-text)',
    lineHeight: '1.4',
  },
});

export function MemoryIndicator({ memoryContext }: MemoryIndicatorProps) {
  const [isOpen, setIsOpen] = useState(false);
  const styles = useStyles();

  if (!memoryContext || memoryContext.entries.length === 0) return null;

  const tooltipContent = (
    <div className={styles.tooltipContent}>
      <div className={styles.tooltipHeader}>
        🧠 Memory Context ({memoryContext.entries.length} {memoryContext.entries.length === 1 ? 'entry' : 'entries'})
      </div>
      {memoryContext.entries.map((entry) => (
        <div key={entry.id} className={styles.tooltipEntry}>
          <span className={styles.tooltipType}>{entry.type}</span>
          <span className={styles.tooltipText}>{entry.content}</span>
        </div>
      ))}
    </div>
  );

  return (
    <Tooltip
      content={tooltipContent}
      relationship="description"
      positioning="above-start"
      visible={isOpen}
      onVisibleChange={(_e, data) => setIsOpen(data.visible)}
    >
      <button
        className={styles.chip}
        onClick={() => setIsOpen(!isOpen)}
        aria-label={`Memory context: ${memoryContext.summary}`}
        data-testid="memory-indicator"
      >
        <span className={styles.icon}>🧠</span>
        <span className={styles.label}>Remembered: {memoryContext.summary}</span>
      </button>
    </Tooltip>
  );
}
