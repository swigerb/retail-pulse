import { useState, useRef, useEffect } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { CARD_COLORS } from '../../constants/agentRouting';
import type { CardComment } from '../../types';

interface CardCommentsProps {
  comments: CardComment[];
  onAddComment: (text: string) => void;
}

function formatTimestamp(ts: string): string {
  const ms = Date.now() - new Date(ts).getTime();
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  header: {
    fontSize: '11px',
    fontWeight: '600',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
  },
  list: {
    maxHeight: '200px',
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    paddingRight: '4px',
    scrollbarWidth: 'thin',
    scrollbarColor: 'rgba(255,255,255,0.1) transparent',
  },
  comment: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '8px 10px',
    borderRadius: '6px',
    background: CARD_COLORS.commentBg,
    border: `1px solid ${CARD_COLORS.cardBorder}`,
  },
  commentMeta: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  commentUser: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  commentTime: {
    fontSize: '10px',
    color: 'var(--color-text-muted)',
  },
  commentText: {
    fontSize: '13px',
    color: 'var(--color-text)',
    lineHeight: '1.5',
  },
  inputRow: {
    display: 'flex',
    gap: '6px',
    alignItems: 'center',
  },
  input: {
    flex: 1,
    padding: '6px 10px',
    borderRadius: '6px',
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    background: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '13px',
    outline: 'none',
    transition: 'border-color 0.2s ease',
    ':focus': {
    },
  },
  submitBtn:{
    padding: '6px 14px',
    borderRadius: '6px',
    border: 'none',
    background: 'rgba(255,255,255,0.1)',
    color: 'var(--color-text)',
    fontSize: '12px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    whiteSpace: 'nowrap',
    ':hover': {
      background: 'rgba(255,255,255,0.16)',
    },
    ':disabled': {
      opacity: 0.4,
      cursor: 'default',
    },
  },
  empty: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
    padding: '12px 0',
  },
});

export default function CardComments({ comments, onAddComment }: CardCommentsProps) {
  const styles = useStyles();
  const [text, setText] = useState('');
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (listRef.current) {
      listRef.current.scrollTop = listRef.current.scrollHeight;
    }
  }, [comments.length]);

  const handleSubmit = () => {
    const trimmed = text.trim();
    if (!trimmed) return;
    onAddComment(trimmed);
    setText('');
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSubmit();
    }
  };

  return (
    <div className={styles.container} data-testid="card-comments">
      <span className={styles.header}>💬 Comments ({comments.length})</span>

      <div className={styles.list} ref={listRef}>
        {comments.length === 0 ? (
          <span className={styles.empty}>No comments yet</span>
        ) : (
          comments.map((c) => (
            <div key={c.id} className={styles.comment} data-testid="comment-item">
              <div className={styles.commentMeta}>
                <span className={styles.commentUser}>{c.userName}</span>
                <span className={styles.commentTime}>{formatTimestamp(c.timestamp)}</span>
              </div>
              <span className={styles.commentText}>{c.text}</span>
            </div>
          ))
        )}
      </div>

      <div className={styles.inputRow}>
        <input
          className={styles.input}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Add a comment…"
          data-testid="comment-input"
        />
        <button
          className={styles.submitBtn}
          onClick={handleSubmit}
          disabled={!text.trim()}
          data-testid="comment-submit"
        >
          Send
        </button>
      </div>
    </div>
  );
}
