import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Button, makeStyles } from '@fluentui/react-components';
import { ChevronLeft24Regular, ChevronRight24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { DEMO_STEPS, type DemoStep, type DemoView } from './demoSteps';

/**
 * Guided walkthrough of the shipped surface.
 *
 * The tour drives the dashboard rather than describing it from the side: each step can
 * switch view and open the telemetry drawer, so the operator is always looking at the
 * thing being described. Advancing is manual — nothing auto-plays, because a demo that
 * moves on its own cannot be paused to answer a question.
 */

interface DemoTourProps {
  readonly open: boolean;
  readonly onClose: () => void;
  /** Switches the dashboard to the view a step wants to talk about. */
  readonly onNavigate: (view: DemoView) => void;
  /** Opens or closes the telemetry drawer for steps that describe it. */
  readonly onTelemetry: (open: boolean) => void;
}

interface Rect {
  readonly top: number;
  readonly left: number;
  readonly width: number;
  readonly height: number;
}

/** Breathing room between the spotlight cutout and the highlighted element. */
const SPOTLIGHT_PADDING = 8;
/** Gap between the cutout and the flyout card. */
const FLYOUT_GAP = 16;
const FLYOUT_WIDTH = 380;
/** Enough for the tallest body copy in the script; the card scrolls beyond this. */
const FLYOUT_MAX_HEIGHT = 340;
/**
 * How long to keep re-measuring the target after a step change. The telemetry drawer
 * animates in over roughly 250ms, so a single post-paint measurement catches it partway
 * through its slide and positions the flyout against a box that no longer exists.
 */
const SETTLE_MS = 700;
const SETTLE_TICK_MS = 80;

/** Rect equality, so the settle loop only re-renders when the target actually moved. */
function sameRect(a: Rect | null, b: Rect | null): boolean {
  if (a === null || b === null) return a === b;
  return a.top === b.top && a.left === b.left && a.width === b.width && a.height === b.height;
}

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: '0',
    zIndex: 9000,
    // Pointer events are enabled so a stray click cannot interact with the app mid-tour,
    // which would desynchronise the narration from what is on screen.
    pointerEvents: 'auto',
  },
  card: {
    position: 'fixed',
    zIndex: 9001,
    width: `${FLYOUT_WIDTH}px`,
    maxHeight: `${FLYOUT_MAX_HEIGHT}px`,
    overflowY: 'auto',
    boxSizing: 'border-box',
    padding: '20px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-bg-elevated, #1a1a24)',
    border: '1px solid var(--brand-accent, #3b82f6)',
    boxShadow: '0 18px 48px rgba(0,0,0,0.55)',
    color: 'var(--color-text, #f1f5f9)',
    fontFamily: "'Inter', 'Segoe UI', system-ui, sans-serif",
  },
  chapter: {
    fontSize: '11px',
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: 'var(--brand-accent, #3b82f6)',
    fontWeight: '600',
  },
  title: {
    fontSize: '17px',
    fontWeight: '600',
    margin: '6px 0 10px',
    lineHeight: '1.3',
  },
  body: {
    fontSize: '13px',
    lineHeight: '1.6',
    color: 'var(--color-text-secondary, #cbd5e1)',
    margin: '0 0 16px',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
  },
  progress: {
    fontSize: '12px',
    color: 'var(--color-text-muted, #94a3b8)',
    fontVariantNumeric: 'tabular-nums',
  },
  actions: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
  },
  rail: {
    display: 'flex',
    gap: '3px',
    marginTop: '14px',
  },
  tick: {
    height: '3px',
    flex: '1',
    borderRadius: '2px',
    backgroundColor: 'var(--color-border, #334155)',
  },
  tickDone: {
    backgroundColor: 'var(--brand-accent, #3b82f6)',
  },
});

/**
 * Reads the on-screen box of a step's target.
 *
 * Returns null when the step has no target or the element is not present — a step whose
 * target is missing degrades to a centred card rather than pointing at the top-left
 * corner, which is what an unchecked getBoundingClientRect would do.
 */
function measure(target: string | undefined): Rect | null {
  if (!target || typeof document === 'undefined') return null;

  const element = document.querySelector(target);
  if (!element) return null;

  const box = element.getBoundingClientRect();
  if (box.width === 0 && box.height === 0) return null;

  return { top: box.top, left: box.left, width: box.width, height: box.height };
}

/** Places the flyout beside the cutout, flipping when it would leave the viewport. */
function positionCard(rect: Rect | null, placement: DemoStep['placement']): { top: number; left: number } {
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(value, max));

  if (!rect || placement === 'center') {
    return {
      top: Math.max(FLYOUT_GAP, (vh - FLYOUT_MAX_HEIGHT) / 2),
      left: clamp((vw - FLYOUT_WIDTH) / 2, FLYOUT_GAP, vw - FLYOUT_WIDTH - FLYOUT_GAP),
    };
  }

  const above = rect.top - FLYOUT_GAP - FLYOUT_MAX_HEIGHT;
  const below = rect.top + rect.height + FLYOUT_GAP;
  const leftOf = rect.left - FLYOUT_GAP - FLYOUT_WIDTH;
  const rightOf = rect.left + rect.width + FLYOUT_GAP;

  let top: number;
  let left: number;

  switch (placement) {
    case 'left':
      // Flip to the other side when there is not enough room, rather than clipping.
      left = leftOf >= FLYOUT_GAP ? leftOf : rightOf;
      top = rect.top;
      break;
    case 'right':
      left = rightOf + FLYOUT_WIDTH <= vw - FLYOUT_GAP ? rightOf : leftOf;
      top = rect.top;
      break;
    case 'top':
      top = above >= FLYOUT_GAP ? above : below;
      left = rect.left + (rect.width - FLYOUT_WIDTH) / 2;
      break;
    default:
      top = below + FLYOUT_MAX_HEIGHT <= vh - FLYOUT_GAP ? below : above;
      left = rect.left + (rect.width - FLYOUT_WIDTH) / 2;
      break;
  }

  return {
    top: clamp(top, FLYOUT_GAP, Math.max(FLYOUT_GAP, vh - FLYOUT_MAX_HEIGHT - FLYOUT_GAP)),
    left: clamp(left, FLYOUT_GAP, Math.max(FLYOUT_GAP, vw - FLYOUT_WIDTH - FLYOUT_GAP)),
  };
}

/**
 * True when the flyout would sit on top of the element it is describing.
 *
 * Clamping a card back inside the viewport can push it over its own spotlight — which is
 * exactly what happened against the telemetry drawer, whose 560px panel is flush with the
 * right edge. When that happens the caller falls back to centring, which is never wrong.
 */
function overlaps(card: { top: number; left: number }, rect: Rect | null): boolean {
  if (!rect) return false;

  return card.left < rect.left + rect.width
    && card.left + FLYOUT_WIDTH > rect.left
    && card.top < rect.top + rect.height
    && card.top + FLYOUT_MAX_HEIGHT > rect.top;
}

export function DemoTour({ open, onClose, onNavigate, onTelemetry }: DemoTourProps) {
  const styles = useStyles();
  const [index, setIndex] = useState(0);
  const [rect, setRect] = useState<Rect | null>(null);
  const cardRef = useRef<HTMLDivElement>(null);

  const step = DEMO_STEPS[index];
  const isFirst = index === 0;
  const isLast = index === DEMO_STEPS.length - 1;

  // Restart from the beginning each time the tour is opened. Resuming mid-tour would be
  // surprising when the button is pressed in front of an audience.
  useEffect(() => {
    if (open) setIndex(0);
  }, [open]);

  // Put the dashboard into the state the step describes BEFORE measuring its target.
  useEffect(() => {
    if (!open || !step) return;
    if (step.view) onNavigate(step.view);
    onTelemetry(Boolean(step.telemetry));
  }, [open, step, onNavigate, onTelemetry]);

  // Measure after the view switch has painted. A layout effect alone is too early: the
  // panel being pointed at may not exist yet on the frame the step changes, and the
  // telemetry drawer slides in over roughly 250ms — measuring it mid-animation captures
  // its off-screen starting position and parks the flyout on top of it.
  useLayoutEffect(() => {
    if (!open || !step) return;

    let cancelled = false;
    let frame = 0;
    let settle = 0;

    const remeasure = () => {
      if (cancelled) return;
      const next = measure(step.target);
      // Only re-render when the box actually moved, so the settle loop does not thrash.
      setRect(prev => (sameRect(prev, next) ? prev : next));
    };

    remeasure();
    frame = window.requestAnimationFrame(remeasure);

    // Keep re-measuring briefly so the final position reflects the settled layout
    // rather than whatever was on screen the instant the step changed.
    const started = Date.now();
    settle = window.setInterval(() => {
      remeasure();
      if (Date.now() - started >= SETTLE_MS) window.clearInterval(settle);
    }, SETTLE_TICK_MS);

    window.addEventListener('resize', remeasure);
    window.addEventListener('scroll', remeasure, true);

    return () => {
      cancelled = true;
      window.cancelAnimationFrame(frame);
      window.clearInterval(settle);
      window.removeEventListener('resize', remeasure);
      window.removeEventListener('scroll', remeasure, true);
    };
  }, [open, step]);

  const next = useCallback(() => {
    setIndex(i => (i >= DEMO_STEPS.length - 1 ? i : i + 1));
  }, []);

  const back = useCallback(() => {
    setIndex(i => (i <= 0 ? 0 : i - 1));
  }, []);

  // Keyboard control: arrows to move, Escape to leave. A demo driven from a lectern is
  // easier with keys than with a trackpad.
  useEffect(() => {
    if (!open) return;

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { onClose(); return; }
      if (e.key === 'ArrowRight' || e.key === 'Enter') { e.preventDefault(); isLast ? onClose() : next(); }
      if (e.key === 'ArrowLeft') { e.preventDefault(); back(); }
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, next, back, onClose, isLast]);

  // Move focus to the card on each step so a screen reader announces the new content and
  // the keyboard handlers have somewhere sensible to live.
  useEffect(() => {
    if (open) cardRef.current?.focus();
  }, [open, index]);

  const position = useMemo(() => {
    if (!open) return { top: 0, left: 0 };

    const placed = positionCard(rect, step?.placement);
    // Never cover the thing being pointed at; centring is the safe fallback.
    return overlaps(placed, rect) ? positionCard(null, 'center') : placed;
  }, [open, rect, step?.placement]);

  if (!open || !step) return null;

  // The cutout is drawn with a huge spread box-shadow rather than an SVG mask: it dims
  // everything outside the highlighted box while leaving the element itself untouched and
  // fully legible.
  const spotlight = rect
    ? {
      position: 'fixed' as const,
      top: `${rect.top - SPOTLIGHT_PADDING}px`,
      left: `${rect.left - SPOTLIGHT_PADDING}px`,
      width: `${rect.width + SPOTLIGHT_PADDING * 2}px`,
      height: `${rect.height + SPOTLIGHT_PADDING * 2}px`,
      borderRadius: '10px',
      boxShadow: '0 0 0 9999px rgba(2, 6, 23, 0.72)',
      border: '2px solid var(--brand-accent, #3b82f6)',
      pointerEvents: 'none' as const,
      transition: 'top 180ms ease, left 180ms ease, width 180ms ease, height 180ms ease',
    }
    : undefined;

  return (
    <>
      <div
        className={styles.overlay}
        data-testid="demo-tour-overlay"
        // Clicking the backdrop does NOT advance or close: an accidental click during a
        // presentation should do nothing rather than skip a step.
        onClick={e => e.stopPropagation()}
        style={rect ? undefined : { backgroundColor: 'rgba(2, 6, 23, 0.72)' }}
      >
        {spotlight && <div style={spotlight} data-testid="demo-tour-spotlight" />}
      </div>

      <div
        ref={cardRef}
        className={styles.card}
        style={{ top: `${position.top}px`, left: `${position.left}px` }}
        role="dialog"
        aria-modal="true"
        aria-label={`Demo Mode: ${step.title}`}
        tabIndex={-1}
        data-testid="demo-tour-card"
      >
        <div className={styles.chapter}>{step.chapter}</div>
        <div className={styles.title} data-testid="demo-tour-title">{step.title}</div>
        <p className={styles.body}>{step.body}</p>

        <div className={styles.footer}>
          <span className={styles.progress} data-testid="demo-tour-progress">
            {index + 1} of {DEMO_STEPS.length}
          </span>
          <div className={styles.actions}>
            <Button
              appearance="subtle"
              icon={<Dismiss24Regular />}
              onClick={onClose}
              data-testid="demo-tour-exit"
            >
              Exit
            </Button>
            <Button
              appearance="secondary"
              icon={<ChevronLeft24Regular />}
              onClick={back}
              disabled={isFirst}
              data-testid="demo-tour-back"
              aria-label="Previous step"
            />
            <Button
              appearance="primary"
              icon={<ChevronRight24Regular />}
              iconPosition="after"
              onClick={isLast ? onClose : next}
              data-testid="demo-tour-next"
            >
              {isLast ? 'Finish' : 'Next'}
            </Button>
          </div>
        </div>

        <div className={styles.rail} aria-hidden="true">
          {DEMO_STEPS.map((s, i) => (
            <div
              key={s.id}
              className={`${styles.tick} ${i <= index ? styles.tickDone : ''}`}
            />
          ))}
        </div>
      </div>
    </>
  );
}

export default DemoTour;
