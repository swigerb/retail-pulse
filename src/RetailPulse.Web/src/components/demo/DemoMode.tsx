import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, makeStyles, Spinner } from '@fluentui/react-components';
import { Dismiss24Regular, Pause24Regular, Play24Regular, ChevronRight24Regular } from '@fluentui/react-icons';
import { DEMO_ACTS, type DemoAct } from './demoActs';
import type { DemoView } from './demoSteps';

/**
 * Automated walkthrough that actually drives the product.
 *
 * The Tour narrates a static surface and waits for a click. This does the opposite: it
 * submits real prompts through the same send path a person typing would use, waits for
 * each answer to land before narrating it, and moves through the views while the results
 * are on screen. The numbers shown during a run are that run's real numbers.
 *
 * Nothing is dimmed. The whole point is to look at the product, so the narration sits in
 * a corner card and the app stays fully visible and interactive behind it.
 */

interface DemoModeProps {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly onNavigate: (view: DemoView) => void;
  readonly onTelemetry: (open: boolean) => void;
  /**
   * Submits a prompt through the live chat pipeline. Null until the chat panel has
   * published its sender, in which case prompt acts narrate without submitting rather
   * than silently appearing to work.
   */
  readonly sendPrompt: ((message: string) => Promise<void>) | null;
}

const useStyles = makeStyles({
  card: {
    position: 'fixed',
    right: '24px',
    bottom: '24px',
    zIndex: 9500,
    width: '420px',
    boxSizing: 'border-box',
    padding: '18px 20px',
    borderRadius: '14px',
    backgroundColor: 'var(--color-bg-elevated, #161622)',
    border: '1px solid var(--brand-accent, #3b82f6)',
    boxShadow: '0 20px 60px rgba(0,0,0,0.6)',
    color: 'var(--color-text, #f1f5f9)',
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '10px',
    marginBottom: '4px',
  },
  chapter: {
    fontSize: '11px',
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: 'var(--brand-accent, #3b82f6)',
    fontWeight: '600',
  },
  liveTag: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '11px',
    fontWeight: '600',
    color: '#22c55e',
  },
  dot: {
    width: '7px',
    height: '7px',
    borderRadius: '50%',
    backgroundColor: '#22c55e',
  },
  title: {
    fontSize: '17px',
    fontWeight: '600',
    margin: '2px 0 8px',
    lineHeight: '1.3',
  },
  body: {
    fontSize: '13px',
    lineHeight: '1.6',
    color: 'var(--color-text-secondary, #cbd5e1)',
    margin: '0 0 12px',
  },
  working: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '12px',
    color: 'var(--brand-accent-light, #93c5fd)',
    margin: '0 0 12px',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '10px',
  },
  progress: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    fontVariantNumeric: 'tabular-nums',
  },
  actions: { display: 'flex', gap: '6px', alignItems: 'center' },
  rail: { display: 'flex', gap: '3px', marginTop: '12px' },
  tick: {
    height: '3px',
    flex: '1',
    borderRadius: '2px',
    backgroundColor: 'var(--color-border, #334155)',
  },
  tickDone: { backgroundColor: 'var(--brand-accent, #3b82f6)' },
});

export function DemoMode({ open, onClose, onNavigate, onTelemetry, sendPrompt }: DemoModeProps) {
  const styles = useStyles();
  const [index, setIndex] = useState(0);
  const [paused, setPaused] = useState(false);
  const [working, setWorking] = useState<string | null>(null);

  // Guards the act runner against double-execution under StrictMode's deliberate
  // double-invoke, which would otherwise submit every prompt twice.
  const runningRef = useRef<string | null>(null);
  const cancelledRef = useRef(false);

  const act: DemoAct | undefined = DEMO_ACTS[index];
  const isLast = index >= DEMO_ACTS.length - 1;

  useEffect(() => {
    if (open) {
      setIndex(0);
      setPaused(false);
      cancelledRef.current = false;
    } else {
      cancelledRef.current = true;
      runningRef.current = null;
      setWorking(null);
    }
  }, [open]);

  const advance = useCallback(() => {
    setIndex(i => (i >= DEMO_ACTS.length - 1 ? i : i + 1));
  }, []);

  const skip = useCallback(() => {
    // Skipping abandons whatever the current act is waiting on rather than queueing
    // behind it, so the button responds immediately.
    cancelledRef.current = true;
    runningRef.current = null;
    setWorking(null);
    if (isLast) { onClose(); return; }
    cancelledRef.current = false;
    advance();
  }, [advance, isLast, onClose]);

  // Runs one act: put the app in the right state, submit any real prompt, hold, advance.
  useEffect(() => {
    if (!open || !act || paused) return;
    if (runningRef.current === act.id) return;
    runningRef.current = act.id;

    let timer = 0;
    let cancelled = false;
    const stillLive = () => !cancelled && !cancelledRef.current;

    void (async () => {
      if (act.view) onNavigate(act.view);
      if (act.telemetry !== undefined) onTelemetry(act.telemetry);

      if (act.prompt) {
        if (!sendPrompt) {
          // Be honest rather than pretending. The message has to survive the hold, so it
          // is deliberately not cleared below — otherwise it would be set and wiped in
          // the same tick and nobody would ever see it.
          setWorking('Chat is not ready — skipping this prompt');
        } else {
          setWorking('Running the prompt…');
          try {
            await sendPrompt(act.prompt);
          } catch {
            // A failed turn should not strand the run; the next act still has value.
          }
          if (!stillLive()) return;
          setWorking(null);
        }
        if (!stillLive()) return;
      }

      // Hold so the result can actually be looked at before moving on.
      timer = window.setTimeout(() => {
        if (!stillLive()) return;
        if (isLast) { onClose(); return; }
        advance();
      }, act.holdMs ?? 5_000);
    })();

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [open, act, paused, sendPrompt, onNavigate, onTelemetry, advance, isLast, onClose]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if (e.key === ' ') { e.preventDefault(); setPaused(p => !p); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open || !act) return null;

  return (
    <div className={styles.card} role="status" aria-live="polite" data-testid="demo-mode-card">
      <div className={styles.head}>
        <span className={styles.chapter}>{act.chapter}</span>
        <span className={styles.liveTag}>
          <span className={styles.dot} />
          LIVE
        </span>
      </div>

      <div className={styles.title} data-testid="demo-mode-title">{act.title}</div>
      <p className={styles.body}>{act.body}</p>

      {working && (
        <div className={styles.working} data-testid="demo-mode-working">
          <Spinner size="tiny" />
          <span>{working}</span>
        </div>
      )}

      <div className={styles.footer}>
        <span className={styles.progress} data-testid="demo-mode-progress">
          {index + 1} of {DEMO_ACTS.length}
        </span>
        <div className={styles.actions}>
          <Button
            appearance="subtle"
            icon={<Dismiss24Regular />}
            onClick={onClose}
            data-testid="demo-mode-exit"
          >
            Stop
          </Button>
          <Button
            appearance="secondary"
            icon={paused ? <Play24Regular /> : <Pause24Regular />}
            onClick={() => setPaused(p => !p)}
            data-testid="demo-mode-pause"
            aria-label={paused ? 'Resume' : 'Pause'}
          />
          <Button
            appearance="primary"
            icon={<ChevronRight24Regular />}
            iconPosition="after"
            onClick={skip}
            data-testid="demo-mode-skip"
          >
            {isLast ? 'Finish' : 'Skip'}
          </Button>
        </div>
      </div>

      <div className={styles.rail} aria-hidden="true">
        {DEMO_ACTS.map((a, i) => (
          <div key={a.id} className={`${styles.tick} ${i <= index ? styles.tickDone : ''}`} />
        ))}
      </div>
    </div>
  );
}

export default DemoMode;
