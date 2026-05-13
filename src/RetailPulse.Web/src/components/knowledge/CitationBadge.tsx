import { useState } from 'react';
import { makeStyles, Badge } from '@fluentui/react-components';
import { KB_COLORS } from '../../constants/agentRouting';
import type { Citation } from '../../types';

interface CitationBadgeProps {
  citation: Citation;
}

const useStyles = makeStyles({
  pill: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '2px 8px',
    borderRadius: '12px',
    backgroundColor: KB_COLORS.citationPill,
    color: KB_COLORS.citationText,
    fontSize: '11px',
    fontWeight: '500',
    cursor: 'pointer',
    transition: 'all 0.15s ease',
    position: 'relative',
    verticalAlign: 'middle',
    ':hover': {
      backgroundColor: 'rgba(6,182,212,0.25)',
    },
  },
  tooltip: {
    position: 'absolute',
    bottom: '100%',
    left: '50%',
    transform: 'translateX(-50%)',
    marginBottom: '6px',
    padding: '10px 14px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-bg-elevated, #1e1b2e)',
    border: '1px solid rgba(6,182,212,0.3)',
    boxShadow: '0 4px 12px rgba(0,0,0,0.4)',
    minWidth: '240px',
    maxWidth: '360px',
    zIndex: 50,
    pointerEvents: 'none',
  },
  tooltipTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text)',
    marginBottom: '6px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  tooltipPreview: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    marginBottom: '6px',
    display: '-webkit-box',
    WebkitLineClamp: 3,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  },
  tooltipScore: {
    fontSize: '10px',
    fontWeight: '600',
  },
  expanded: {
    marginTop: '8px',
    padding: '12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(6,182,212,0.06)',
    border: '1px solid rgba(6,182,212,0.15)',
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.6',
  },
  expandedTitle: {
    fontSize: '12px',
    fontWeight: '600',
    color: KB_COLORS.citationText,
    marginBottom: '6px',
  },
});

function getScoreColor(score: number): string {
  if (score >= 0.8) return KB_COLORS.relevanceHigh;
  if (score >= 0.5) return KB_COLORS.relevanceMedium;
  return KB_COLORS.relevanceLow;
}

export default function CitationBadge({ citation }: CitationBadgeProps) {
  const styles = useStyles();
  const [hovered, setHovered] = useState(false);
  const [expanded, setExpanded] = useState(false);

  return (
    <>
      <span
        className={styles.pill}
        data-testid="citation-badge"
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        onClick={() => setExpanded(prev => !prev)}
        role="button"
        tabIndex={0}
        aria-label={`Citation: ${citation.sourceName}`}
        onKeyDown={e => e.key === 'Enter' && setExpanded(prev => !prev)}
      >
        📖 {citation.sourceName}
        <Badge
          appearance="filled"
          style={{
            background: `${getScoreColor(citation.relevanceScore)}20`,
            color: getScoreColor(citation.relevanceScore),
            fontSize: '9px',
            padding: '0 4px',
            minWidth: 'auto',
          }}
        >
          {(citation.relevanceScore * 100).toFixed(0)}%
        </Badge>

        {hovered && (
          <div className={styles.tooltip} data-testid="citation-tooltip">
            <div className={styles.tooltipTitle}>{citation.sourceTitle}</div>
            <div className={styles.tooltipPreview}>{citation.chunkPreview}</div>
            <div className={styles.tooltipScore} style={{ color: getScoreColor(citation.relevanceScore) }}>
              Relevance: {(citation.relevanceScore * 100).toFixed(0)}%
            </div>
          </div>
        )}
      </span>

      {expanded && (
        <div className={styles.expanded} data-testid="citation-expanded">
          <div className={styles.expandedTitle}>{citation.sourceTitle}</div>
          <div>{citation.chunkPreview}</div>
        </div>
      )}
    </>
  );
}
