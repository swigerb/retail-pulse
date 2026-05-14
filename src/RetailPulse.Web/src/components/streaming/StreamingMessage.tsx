import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { makeStyles } from '@fluentui/react-components';

export interface StreamingMessageProps {
  /** Full accumulated tokens so far */
  tokens: string;
  /** Whether the stream is still in progress */
  isStreaming: boolean;
  /** Callback when streaming completes */
  onComplete?: () => void;
}

const useStyles = makeStyles({
  container: {
    position: 'relative',
  },
  cursor: {
    display: 'inline-block',
    width: '2px',
    height: '1.1em',
    backgroundColor: 'var(--brand-accent, #60a5fa)',
    marginLeft: '2px',
    verticalAlign: 'text-bottom',
    animationName: {
      '0%, 100%': { opacity: 1 },
      '50%': { opacity: 0 },
    },
    animationDuration: '800ms',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'step-end',
  },
  generating: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '13px',
    color: 'var(--color-text-muted, #94a3b8)',
    padding: '4px 0',
  },
  dot: {
    display: 'inline-block',
    width: '6px',
    height: '6px',
    borderRadius: '50%',
    backgroundColor: 'var(--brand-accent, #60a5fa)',
    animationName: {
      '0%, 80%, 100%': { opacity: 0.3, transform: 'scale(0.8)' },
      '40%': { opacity: 1, transform: 'scale(1.2)' },
    },
    animationDuration: '1.2s',
    animationIterationCount: 'infinite',
  },
  dot2: {
    animationDelay: '0.15s',
  },
  dot3: {
    animationDelay: '0.3s',
  },
});

const TICK_MS = 18;

export function StreamingMessage({ tokens, isStreaming, onComplete }: StreamingMessageProps) {
  const styles = useStyles();
  const [displayedLength, setDisplayedLength] = useState(0);
  const completedRef = useRef(false);
  const targetRef = useRef(tokens.length);
  const lastTickRef = useRef(0);
  const rafRef = useRef<number | null>(null);

  // Keep target length in sync with incoming tokens
  targetRef.current = tokens.length;

  const tick = useCallback((now: number) => {
    if (now - lastTickRef.current < TICK_MS) {
      rafRef.current = requestAnimationFrame(tick);
      return;
    }
    lastTickRef.current = now;

    setDisplayedLength(prev => {
      const target = targetRef.current;
      if (prev >= target) return prev;
      const remaining = target - prev;
      const step = remaining > 20 ? Math.min(remaining, 5) : 1;
      return Math.min(prev + step, target);
    });

    rafRef.current = requestAnimationFrame(tick);
  }, []);

  // Single animation loop — starts once, cleans up on unmount
  useEffect(() => {
    rafRef.current = requestAnimationFrame(tick);
    return () => {
      if (rafRef.current !== null) cancelAnimationFrame(rafRef.current);
    };
  }, [tick]);

  // Fire onComplete once all tokens are revealed and streaming is done
  useEffect(() => {
    if (displayedLength >= tokens.length && !isStreaming && !completedRef.current) {
      completedRef.current = true;
      onComplete?.();
    }
  }, [displayedLength, tokens.length, isStreaming, onComplete]);

  const displayedText = useMemo(
    () => tokens.slice(0, displayedLength),
    [tokens, displayedLength],
  );

  const showGenerating = isStreaming && tokens.length === 0;
  const showCursor = isStreaming && tokens.length > 0;

  if (showGenerating) {
    return (
      <div className={styles.generating} data-testid="streaming-generating">
        <span className={styles.dot} />
        <span className={`${styles.dot} ${styles.dot2}`} />
        <span className={`${styles.dot} ${styles.dot3}`} />
        <span>Generating...</span>
      </div>
    );
  }

  return (
    <div className={styles.container} data-testid="streaming-message">
      <div className="markdown-body">
        <ReactMarkdown remarkPlugins={[remarkGfm]}>{displayedText}</ReactMarkdown>
        {showCursor && <span className={styles.cursor} data-testid="streaming-cursor" />}
      </div>
    </div>
  );
}
