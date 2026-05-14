import { useState } from 'react';
import { Text, makeStyles } from '@fluentui/react-components';

interface CollapsibleSectionProps {
  title: string;
  defaultExpanded?: boolean;
  children: React.ReactNode;
}

const useStyles = makeStyles({
  section: {
    marginBottom: '16px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 4px',
    cursor: 'pointer',
    userSelect: 'none',
    borderRadius: '6px',
    ':hover': {
      background: 'rgba(255,255,255,0.04)',
    },
  },
  chevron: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '20px',
    height: '20px',
    fontSize: '12px',
    color: 'var(--color-text-subtle)',
    transition: 'transform 0.2s ease',
  },
  chevronExpanded: {
    transform: 'rotate(90deg)',
  },
  title: {
    fontSize: '13px',
    fontWeight: '600',
    color: 'var(--color-text)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  content: {
    overflow: 'hidden',
    transition: 'max-height 0.25s ease, opacity 0.2s ease',
  },
  contentCollapsed: {
    maxHeight: '0px',
    opacity: '0',
    pointerEvents: 'none',
  },
  contentExpanded: {
    maxHeight: '5000px',
    opacity: '1',
  },
});

export function CollapsibleSection({ title, defaultExpanded = false, children }: CollapsibleSectionProps) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(defaultExpanded);

  return (
    <div className={styles.section}>
      <div
        className={styles.header}
        onClick={() => setExpanded(prev => !prev)}
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(prev => !prev); } }}
      >
        <span className={`${styles.chevron} ${expanded ? styles.chevronExpanded : ''}`}>
          ▶
        </span>
        <Text className={styles.title}>{title}</Text>
      </div>
      <div className={`${styles.content} ${expanded ? styles.contentExpanded : styles.contentCollapsed}`}>
        {children}
      </div>
    </div>
  );
}
