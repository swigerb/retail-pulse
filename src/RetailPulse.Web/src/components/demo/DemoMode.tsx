import { useCallback, useEffect, useRef, useState } from 'react';
import { Button, makeStyles, Spinner } from '@fluentui/react-components';
import {
  ChevronLeft24Regular, ChevronRight24Regular, Dismiss24Regular, Pause24Regular, Play24Regular,
} from '@fluentui/react-icons';
import { DEMO_ACTS, type DemoAct, type DemoInteraction } from './demoActs';
import type { DemoView } from './demoSteps';

/**
 * Automated walkthrough that actually drives the product.
 *
 * The Tour narrates a static surface and waits for a click. This does the opposite: it
 * submits real prompts through the same send path a person typing would use, clicks the
 * same controls inside each panel that an operator would, and waits for the work to finish
 * before narrating the result.
 *
 * Nothing is dimmed. The whole point is to look at the product, so the narration sits in a
 * corner card and the app stays fully visible and interactive behind it.
 */

interface DemoModeProps {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly onNavigate: (view: DemoView) => void;
  readonly onTelemetry: (open: boolean) => void;
  /**
   * Submits a prompt through the live chat pipeline. Null until the chat panel has
   * published its sender, in which case prompt acts say so rather than appearing to work.
   */
  readonly sendPrompt: ((message: string) => Promise<void>) | null;
  /** Whether the telemetry drawer is open, so the card can stay clear of it. */
  readonly telemetryOpen: boolean;
}

/** Width of the telemetry drawer, so the card sits beside it rather than behind it. */
const DRAWER_WIDTH_PX = 560;
const EDGE_GAP_PX = 24;

const useStyles = makeStyles({
  card: {
    position: 'fixed',
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
    transition: 'right 260ms cubic-bezier(0.4, 0, 0.2, 1)',
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
  dot: { width: '7px', height: '7px', borderRadius: '50%', backgroundColor: '#22c55e' },
  title: { fontSize: '17px', fontWeight: '600', margin: '2px 0 8px', lineHeight: '1.3' },
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

/** Sets a React-controlled input's value so the component's onChange actually fires. */
function setNativeValue(element: HTMLElement, value: string): void {
  const input = element as HTMLInputElement | HTMLTextAreaElement;
  const proto = input instanceof HTMLTextAreaElement
    ? HTMLTextAreaElement.prototype
    : HTMLInputElement.prototype;

  const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
  if (!setter) return;

  setter.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

export function DemoMode({
  open, onClose, onNavigate, onTelemetry, sendPrompt, telemetryOpen,
}: DemoModeProps) {
  const styles = useStyles();
  const [index, setIndex] = useState(0);
  const [paused, setPaused] = useState(false);
  const [working, setWorking] = useState<string | null>(null);

  // Callbacks live in refs so the act runner can depend ONLY on the act index. Taking them
  // as effect dependencies made the runner re-enter whenever the host re-rendered with a
  // new inline callback, which submitted the same prompt three times in a row.
  const navigateRef = useRef(onNavigate);
  const telemetryRef = useRef(onTelemetry);
  const sendRef = useRef(sendPrompt);
  const closeRef = useRef(onClose);
  navigateRef.current = onNavigate;
  telemetryRef.current = onTelemetry;
  sendRef.current = sendPrompt;
  closeRef.current = onClose;

  /** Bumped whenever the run jumps, so an in-flight act abandons its remaining work. */
  const runToken = useRef(0);

  const act: DemoAct | undefined = DEMO_ACTS[index];
  const isFirst = index === 0;
  const isLast = index >= DEMO_ACTS.length - 1;

  useEffect(() => {
    if (open) {
      setIndex(0);
      setPaused(false);
    }
    runToken.current += 1;
    setWorking(null);
  }, [open]);

  const goTo = useCallback((target: number) => {
    // Abandon whatever the current act is waiting on so the button responds immediately
    // rather than queueing behind a 25 second council vote.
    runToken.current += 1;
    setWorking(null);
    setIndex(Math.max(0, Math.min(target, DEMO_ACTS.length - 1)));
  }, []);

  const next = useCallback(() => {
    if (isLast) { onClose(); return; }
    goTo(index + 1);
  }, [goTo, index, isLast, onClose]);

  const back = useCallback(() => goTo(index - 1), [goTo, index]);

  // Runs one act: put the app in the right state, submit any prompt, drive any controls,
  // hold, then advance. Depends only on the act index and pause state.
  useEffect(() => {
    if (!open || !act || paused) return;

    runToken.current += 1;
    const token = runToken.current;
    const live = () => runToken.current === token;

    let timer = 0;
    const pause = (ms: number) => new Promise<void>(resolve => {
      timer = window.setTimeout(resolve, ms);
    });

    const runInteraction = async (step: DemoInteraction) => {
      if (step.note) setWorking(step.note);

      if (step.kind === 'wait') {
        await pause(step.ms);
        return;
      }

      const target = document.querySelector<HTMLElement>(step.selector);
      // A missing control is skipped rather than thrown: a panel that has not finished
      // loading must not take the whole run down in front of an audience.
      if (!target) return;

      if (step.kind === 'click') {
        target.click();
        await pause(400);
        return;
      }

      setNativeValue(target, step.text);
      await pause(300);
    };

    void (async () => {
      if (act.view) navigateRef.current(act.view);
      if (act.telemetry !== undefined) telemetryRef.current(act.telemetry);

      // When the sender is unavailable the honest message must survive to the hold, so
      // the post-interaction reset below is suppressed for that case. Clearing it there
      // was why the fallback was never actually visible.
      let keepWorking = false;

      if (act.prompt) {
        const send = sendRef.current;
        if (!send) {
          setWorking('Chat is not ready, skipping this prompt');
          keepWorking = true;
        } else {
          setWorking('Running the prompt');
          try {
            await send(act.prompt);
          } catch {
            // A failed turn should not strand the run; the next act still has value.
          }
          if (!live()) return;
          setWorking(null);
        }
        if (!live()) return;
      }

      for (const step of act.interactions ?? []) {
        if (!live()) return;
        await runInteraction(step);
        keepWorking = false;
      }
      if (!live()) return;
      if (!keepWorking) setWorking(null);

      await pause(act.holdMs ?? 5_000);
      if (!live()) return;

      if (isLast) { closeRef.current(); return; }
      setIndex(i => (i === index ? i + 1 : i));
    })();

    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, index, paused]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if (e.key === ' ') { e.preventDefault(); setPaused(p => !p); }
      if (e.key === 'ArrowRight') { e.preventDefault(); next(); }
      if (e.key === 'ArrowLeft') { e.preventDefault(); back(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose, next, back]);

  if (!open || !act) return null;

  // Slide clear of the telemetry drawer instead of hiding underneath it.
  const right = telemetryOpen ? DRAWER_WIDTH_PX + EDGE_GAP_PX : EDGE_GAP_PX;

  return (
    <div
      className={styles.card}
      style={{ right: `${right}px` }}
      role="status"
      aria-live="polite"
      data-testid="demo-mode-card"
    >
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
            appearance="secondary"
            icon={<ChevronLeft24Regular />}
            onClick={back}
            disabled={isFirst}
            data-testid="demo-mode-back"
            aria-label="Previous"
          />
          <Button
            appearance="primary"
            icon={<ChevronRight24Regular />}
            iconPosition="after"
            onClick={next}
            data-testid="demo-mode-next"
          >
            {isLast ? 'Finish' : 'Next'}
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
