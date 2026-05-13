import { useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { CARD_COLORS } from '../../constants/agentRouting';
import type { AdaptiveCard, VoteChoice } from '../../types';
import CardLifecycleIndicator from './CardLifecycleIndicator';
import CardComments from './CardComments';
import EscalationBanner from './EscalationBanner';

interface VotingCardProps {
  card: AdaptiveCard;
  currentUserId: string;
  onVote: (choice: VoteChoice) => void;
}

const VOTE_BUTTONS: Array<{ choice: VoteChoice; emoji: string; label: string; color: string }> = [
  { choice: 'approve', emoji: '✅', label: 'Approve', color: CARD_COLORS.approve },
  { choice: 'reject', emoji: '❌', label: 'Reject', color: CARD_COLORS.reject },
  { choice: 'abstain', emoji: '⏭️', label: 'Abstain', color: CARD_COLORS.abstain },
];

const useStyles = makeStyles({
  card: {
    background: CARD_COLORS.cardBg,
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    transition: 'all 0.3s ease',
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  title: {
    fontSize: '17px',
    fontWeight: '700',
    color: 'var(--color-text)',
    lineHeight: '1.3',
  },
  summary: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
  },
  voteButtons: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
  },
  voteBtn: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 16px',
    borderRadius: '8px',
    border: '1px solid rgba(255,255,255,0.1)',
    background: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.1)',
      transform: 'translateY(-1px)',
    },
    ':disabled': {
      opacity: 0.4,
      cursor: 'default',
      transform: 'none',
    },
  },
  votedBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '6px 14px',
    borderRadius: '8px',
    fontSize: '13px',
    fontWeight: '600',
  },
  tallyContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  tallyLabel: {
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  tallyBar: {
    display: 'flex',
    height: '8px',
    borderRadius: '4px',
    overflow: 'hidden',
    background: 'rgba(255,255,255,0.06)',
  },
  tallyApprove: {
    height: '100%',
    background: CARD_COLORS.approve,
    transition: 'width 0.6s cubic-bezier(0.34, 1.56, 0.64, 1)',
  },
  tallyReject: {
    height: '100%',
    background: CARD_COLORS.reject,
    transition: 'width 0.6s cubic-bezier(0.34, 1.56, 0.64, 1)',
  },
  tallyCounts: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
  },
  voterPills: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px',
  },
  voterPill: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '10px',
    border: '1px solid rgba(255,255,255,0.08)',
    whiteSpace: 'nowrap',
  },
  splitWarning: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 14px',
    borderRadius: '8px',
    background: CARD_COLORS.escalationBg,
    border: `1px solid ${CARD_COLORS.escalation}40`,
    fontSize: '13px',
    fontWeight: '600',
    color: CARD_COLORS.escalation,
  },
});

export default function VotingCard({ card, currentUserId, onVote }: VotingCardProps) {
  const styles = useStyles();
  const votes = card.votes ?? [];
  const comments = card.comments ?? [];

  const userVote = useMemo(
    () => votes.find((v) => v.userId === currentUserId),
    [votes, currentUserId],
  );

  const tally = useMemo(() => {
    const approve = votes.filter((v) => v.choice === 'approve').length;
    const reject = votes.filter((v) => v.choice === 'reject').length;
    const abstain = votes.filter((v) => v.choice === 'abstain').length;
    const total = approve + reject + abstain;
    return { approve, reject, abstain, total };
  }, [votes]);

  const isSplitVote = useMemo(() => {
    if (tally.total === 0) return false;
    const approveRatio = tally.approve / tally.total;
    const rejectRatio = tally.reject / tally.total;
    return Math.abs(approveRatio - rejectRatio) <= 0.1 && tally.approve > 0 && tally.reject > 0;
  }, [tally]);

  const approveWidth = tally.total > 0 ? (tally.approve / tally.total) * 100 : 0;
  const rejectWidth = tally.total > 0 ? (tally.reject / tally.total) * 100 : 0;

  const choiceColor = (choice: VoteChoice) =>
    choice === 'approve' ? CARD_COLORS.approve : choice === 'reject' ? CARD_COLORS.reject : CARD_COLORS.abstain;

  return (
    <div className={styles.card} data-testid="voting-card">
      <div className={styles.header}>
        <span className={styles.title}>{card.title}</span>
        <span className={styles.summary}>{card.summary}</span>
      </div>

      <CardLifecycleIndicator currentState={card.state} stateChangedAt={card.stateChangedAt} />

      {card.escalated && card.escalationReason && (
        <EscalationBanner reason={card.escalationReason} />
      )}

      {/* Vote buttons or voted badge */}
      {userVote ? (
        <div
          className={styles.votedBadge}
          style={{
            background: `${choiceColor(userVote.choice)}20`,
            color: choiceColor(userVote.choice),
            border: `1px solid ${choiceColor(userVote.choice)}40`,
          }}
          data-testid="voted-badge"
        >
          You voted: {userVote.choice.charAt(0).toUpperCase() + userVote.choice.slice(1)}
        </div>
      ) : (
        <div className={styles.voteButtons}>
          {VOTE_BUTTONS.map((btn) => (
            <button
              key={btn.choice}
              className={styles.voteBtn}
              onClick={() => onVote(btn.choice)}
              style={{ borderColor: `${btn.color}40` }}
              data-testid={`vote-btn-${btn.choice}`}
            >
              <span>{btn.emoji}</span> {btn.label}
            </button>
          ))}
        </div>
      )}

      {/* Vote tally */}
      {tally.total > 0 && (
        <div className={styles.tallyContainer} data-testid="vote-tally">
          <span className={styles.tallyLabel}>Vote Tally</span>
          <div className={styles.tallyBar}>
            <div className={styles.tallyApprove} style={{ width: `${approveWidth}%` }} />
            <div className={styles.tallyReject} style={{ width: `${rejectWidth}%` }} />
          </div>
          <div className={styles.tallyCounts}>
            <span style={{ color: CARD_COLORS.approve }}>✅ {tally.approve}</span>
            <span style={{ color: CARD_COLORS.abstain }}>⏭️ {tally.abstain}</span>
            <span style={{ color: CARD_COLORS.reject }}>❌ {tally.reject}</span>
          </div>
        </div>
      )}

      {/* Split vote warning */}
      {isSplitVote && (
        <div className={styles.splitWarning} data-testid="split-vote-warning">
          <span>⚖️</span> Split Vote — Escalation pending
        </div>
      )}

      {/* Voter pills */}
      {votes.length > 0 && (
        <div className={styles.voterPills} data-testid="voter-pills">
          {votes.map((v) => (
            <span
              key={v.userId}
              className={styles.voterPill}
              style={{
                background: `${choiceColor(v.choice)}15`,
                color: choiceColor(v.choice),
              }}
            >
              {v.userName}
            </span>
          ))}
        </div>
      )}

      {/* Comments section */}
      <CardComments
        comments={comments}
        onAddComment={() => {
          /* handled by parent via SignalR */
        }}
      />
    </div>
  );
}
