import { useState } from 'react';
import { makeStyles, Badge } from '@fluentui/react-components';
import { KB_COLORS } from '../../constants/agentRouting';
import type { KBSearchResult, KnowledgeRelevanceKind } from '../../types';

interface SearchResultsProps {
  results: KBSearchResult[];
  query: string;
  relevanceKind?: KnowledgeRelevanceKind;
  scoreSemantics?: string;
}

const useStyles = makeStyles({
  wrapper: {
    padding: '16px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
    borderRadius: '12px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '12px',
    flexWrap: 'wrap',
  },
  title: {
    fontSize: '14px',
    fontWeight: '600',
    color: '#06b6d4' as const,
  },
  relevanceNote: {
    flexBasis: '100%',
    fontSize: '11px',
    color: 'var(--color-text-muted, #94a3b8)',
    lineHeight: '1.5',
  },
  resultList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  resultCard: {
    padding: '12px 14px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.06)',
    cursor: 'pointer',
    transition: 'all 0.15s',
  },
  resultHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '6px',
  },
  resultTitle: {
    fontSize: '13px',
    fontWeight: '600',
    color: 'var(--color-text)',
    flex: 1,
  },
  resultPreview: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
    overflow: 'hidden',
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
  },
  resultExpanded: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.6',
    marginTop: '8px',
    padding: '10px',
    borderRadius: '6px',
    backgroundColor: 'rgba(6,182,212,0.04)',
    border: '1px solid rgba(6,182,212,0.1)',
  },
  empty: {
    padding: '32px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
  suggestion: {
    fontSize: '12px',
    color: '#94a3b8',
    marginTop: '8px',
  },
});

function getScoreColor(score: number): string {
  if (score >= 0.8) return KB_COLORS.relevanceHigh;
  if (score >= 0.5) return KB_COLORS.relevanceMedium;
  return KB_COLORS.relevanceLow;
}

function getRelevanceLabel(kind: KnowledgeRelevanceKind | undefined): string {
  switch (kind) {
    case 'Semantic': return 'similarity';
    case 'Hybrid': return 'hybrid score';
    case 'Lexical': return 'BM25 score';
    default: return 'score';
  }
}

function getScoreBand(score: number): 'high' | 'medium' | 'low' {
  if (score >= 0.8) return 'high';
  if (score >= 0.5) return 'medium';
  return 'low';
}

function ResultCard({ result, relevanceKind }: {
  result: KBSearchResult;
  relevanceKind?: KnowledgeRelevanceKind;
}) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const label = getRelevanceLabel(relevanceKind);
  const band = getScoreBand(result.score);

  return (
    <div
      className={styles.resultCard}
      data-testid="search-result"
      data-relevance-band={band}
      onClick={() => setExpanded(prev => !prev)}
      role="button"
      tabIndex={0}
      onKeyDown={e => e.key === 'Enter' && setExpanded(prev => !prev)}
    >
      <div className={styles.resultHeader}>
        <span className={styles.resultTitle}>📄 {result.title}</span>
        <Badge
          appearance="filled"
          style={{
            background: `${getScoreColor(result.score)}20`,
            color: getScoreColor(result.score),
            fontSize: '10px',
          }}
          aria-label={`Relevance band: ${band}, ${(result.score * 100).toFixed(0)}% ${label}`}
        >
          {(result.score * 100).toFixed(0)}% {label}
        </Badge>
      </div>
      <div className={styles.resultPreview}>{result.chunk}</div>
      {expanded && (
        <div className={styles.resultExpanded} data-testid="result-expanded">
          <div style={{ fontWeight: 600, marginBottom: 4, color: 'var(--color-text)' }}>
            Chunk #{result.chunkIndex + 1}
          </div>
          {result.chunk}
        </div>
      )}
    </div>
  );
}

export default function SearchResults({ results, query, relevanceKind, scoreSemantics }: SearchResultsProps) {
  const styles = useStyles();
  const relevanceHeading = relevanceKind
    ? `${relevanceKind} relevance`
    : 'Relevance';

  return (
    <div className={styles.wrapper} data-testid="search-results">
      <div className={styles.titleRow}>
        <span className={styles.title}>🔍 Search Results</span>
        <Badge appearance="filled" style={{ background: 'rgba(6,182,212,0.15)', color: '#67e8f9' }}>
          {results.length} {results.length === 1 ? 'result' : 'results'}
        </Badge>
        <Badge
          appearance="outline"
          data-testid="search-relevance-kind"
          aria-label={`Relevance semantics: ${relevanceHeading}`}
        >
          {relevanceHeading}
        </Badge>
        {scoreSemantics && (
          <div className={styles.relevanceNote} data-testid="search-score-semantics">
            {scoreSemantics}
          </div>
        )}
      </div>

      {results.length === 0 ? (
        <div className={styles.empty} data-testid="no-results">
          <div>No results found for &ldquo;{query}&rdquo;</div>
          <div className={styles.suggestion}>
            💡 Try broader terms, check spelling, or upload more documents to the knowledge base.
          </div>
        </div>
      ) : (
        <div className={styles.resultList}>
          {results.map((r, idx) => (
            <ResultCard
              key={`${r.documentId}-${r.chunkIndex}-${idx}`}
              result={r}
              relevanceKind={relevanceKind}
            />
          ))}
        </div>
      )}
    </div>
  );
}
