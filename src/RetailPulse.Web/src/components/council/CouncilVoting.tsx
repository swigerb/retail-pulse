import { makeStyles } from '@fluentui/react-components';
import { COUNCIL_COLORS } from '../../constants/agentRouting';
import type { CouncilAgentVote } from '../../types';
import VoteCard, { VoteCardThinking } from './VoteCard';

interface CouncilVotingProps {
  votes: CouncilAgentVote[];
  loading?: boolean;
}

const DOMAINS = ['demand', 'supply', 'competitive'] as const;

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
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
  subtitle: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
  },
  votesRow: {
    display: 'flex',
    gap: '16px',
    flexWrap: 'wrap',
  },
  divider: {
    height: '1px',
    background: COUNCIL_COLORS.cardBorder,
    marginTop: '8px',
  },
});

export default function CouncilVoting({ votes, loading }: CouncilVotingProps) {
  const styles = useStyles();

  const votesByDomain = new Map(votes.map(v => [v.domain, v]));

  return (
    <div className={styles.container} data-testid="council-voting">
      <div className={styles.titleRow}>
        <span className={styles.title}>🗳️ Specialist Votes</span>
        {loading && <span className={styles.subtitle}>Agents deliberating...</span>}
        {!loading && votes.length > 0 && (
          <span className={styles.subtitle}>{votes.filter(v => !v.timedOut).length} of 3 reporting</span>
        )}
      </div>

      <div className={styles.votesRow}>
        {DOMAINS.map((domain, i) => {
          const vote = votesByDomain.get(domain);
          if (vote) {
            return <VoteCard key={domain} vote={vote} index={i} />;
          }
          if (loading) {
            return <VoteCardThinking key={domain} domain={domain} index={i} />;
          }
          return null;
        })}
      </div>

      <div className={styles.divider} />
    </div>
  );
}
