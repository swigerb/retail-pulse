import { useState, useCallback } from 'react';
import { makeStyles, Badge, Button } from '@fluentui/react-components';
import { ChevronDown16Regular, ChevronUp16Regular } from '@fluentui/react-icons';
import { COUNCIL_COLORS } from '../../constants/agentRouting';
import type { CouncilSession, HealthRating } from '../../types';
import { fetchCouncilHistory } from '../../services/councilApi';

const RATING_COLORS: Record<HealthRating, string> = {
  green: COUNCIL_COLORS.green,
  yellow: COUNCIL_COLORS.yellow,
  red: COUNCIL_COLORS.red,
};

const RATING_EMOJIS: Record<HealthRating, string> = {
  green: '🟢',
  yellow: '🟡',
  red: '🔴',
};

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    marginTop: '20px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  item: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    background: COUNCIL_COLORS.cardBg,
    border: `1px solid ${COUNCIL_COLORS.cardBorder}`,
    borderRadius: '8px',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  ratingDot: {
    width: '12px',
    height: '12px',
    borderRadius: '50%',
    flexShrink: 0,
  },
  itemInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  brand: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  date: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
  },
  badges: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
  },
  badge: {
    fontSize: '10px',
    padding: '2px 8px',
    borderRadius: '4px',
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  expandedContent: {
    padding: '12px 16px',
    background: 'rgba(255,255,255,0.02)',
    borderRadius: '0 0 8px 8px',
    borderTop: `1px solid ${COUNCIL_COLORS.cardBorder}`,
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.6',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
  loadBtn: {
    alignSelf: 'center',
  },
});

function HistoryItem({ session }: { session: CouncilSession }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const v = session.verdict;
  const date = new Date(session.convenedAt);
  const displayDate = Number.isNaN(date.getTime()) ? 'Unknown date' : date.toLocaleString();

  return (
    <div>
      <div
        className={styles.item}
        onClick={() => setExpanded(prev => !prev)}
        data-testid="history-item"
        role="button"
        tabIndex={0}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') setExpanded(prev => !prev); }}
      >
        <div
          className={styles.ratingDot}
          style={{ background: RATING_COLORS[v.overallRating] }}
        />
        <div className={styles.itemInfo}>
          <span className={styles.brand}>
            {RATING_EMOJIS[v.overallRating]} {session.brand}
            {session.region && ` · ${session.region}`}
          </span>
          <span className={styles.date}>{displayDate}</span>
        </div>
        <div className={styles.badges}>
          <Badge
            className={styles.badge}
            appearance="filled"
            style={{
              background: v.unanimous ? `${COUNCIL_COLORS.green}25` : `${COUNCIL_COLORS.yellow}25`,
              color: v.unanimous ? COUNCIL_COLORS.green : COUNCIL_COLORS.yellow,
            }}
          >
            {v.unanimous ? '✓ Unanimous' : '⚠️ Split'}
          </Badge>
        </div>
        {expanded ? <ChevronUp16Regular /> : <ChevronDown16Regular />}
      </div>
      {expanded && (
        <div className={styles.expandedContent} data-testid="history-expanded">
          <strong>Synthesis:</strong> {v.synthesisText}
          {v.actionItems.length > 0 && (
            <div style={{ marginTop: '8px' }}>
              <strong>Actions:</strong>
              <ol style={{ margin: '4px 0', paddingLeft: '20px' }}>
                {v.actionItems.map((a, i) => (
                  <li key={i}>{a.text}</li>
                ))}
              </ol>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export default function CouncilHistory() {
  const styles = useStyles();
  const [sessions, setSessions] = useState<CouncilSession[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const handleLoad = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const data = await fetchCouncilHistory();
      setSessions(data);
      setLoaded(true);
    } catch {
      setSessions([]);
      setLoadError('Unable to load previous council sessions. Please try again.');
      setLoaded(true);
    } finally {
      setLoading(false);
    }
  }, []);

  return (
    <div className={styles.container} data-testid="council-history">
      <div className={styles.titleRow}>
        <span className={styles.title}>📜 Previous Assessments</span>
      </div>

      {!loaded ? (
        <Button
          className={styles.loadBtn}
          appearance="subtle"
          onClick={handleLoad}
          disabled={loading}
        >
          {loading ? '⏳ Loading...' : 'Load History'}
        </Button>
      ) : loadError ? (
        <div className={styles.empty} data-testid="history-error">
          <div>{loadError}</div>
          <Button
            className={styles.loadBtn}
            appearance="subtle"
            onClick={handleLoad}
            disabled={loading}
          >
            {loading ? '⏳ Loading...' : 'Try again'}
          </Button>
        </div>
      ) : sessions.length === 0 ? (
        <div className={styles.empty} data-testid="history-empty">
          No previous council sessions found
        </div>
      ) : (
        <div className={styles.list}>
          {sessions.map(s => (
            <HistoryItem key={s.id} session={s} />
          ))}
        </div>
      )}
    </div>
  );
}
