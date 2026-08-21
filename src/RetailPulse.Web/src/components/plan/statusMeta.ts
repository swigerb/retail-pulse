import type { PlanStatus, PlanStepStatus } from '../../types';

/**
 * Central lookup for how the plan UI labels a status, its icon glyph, and
 * the theme token to color the status pill. Kept in one place so the reducer,
 * PlanView, and PlanHistory render the same words and glyphs. Color is
 * expressed as a CSS variable so tenant theming (`--brand-*` tokens) drives
 * the palette — no hardcoded hex ever leaks into the component.
 */

export interface StatusMeta {
  label: string;
  /** Icon glyph shown to convey status beyond color alone (a11y requirement). */
  icon: string;
  /** CSS variable for the pill's foreground color. */
  fg: string;
  /** CSS variable for the pill's background color. */
  bg: string;
  /** CSS variable for the pill's border color. */
  border: string;
}

export const PLAN_STATUS_META: Record<PlanStatus, StatusMeta> = {
  draft: {
    label: 'Drafting',
    icon: '📝',
    fg: 'var(--color-text-muted)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
  running: {
    label: 'Running',
    icon: '▶︎',
    fg: 'var(--brand-accent)',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  awaiting_review: {
    label: 'Awaiting review',
    icon: '⏸',
    fg: 'var(--brand-primary)',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  awaiting_clarification: {
    label: 'Needs clarification',
    icon: '❓',
    fg: 'var(--brand-primary)',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  completed: {
    label: 'Completed',
    icon: '✓',
    fg: 'var(--color-success, var(--brand-accent))',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  failed: {
    label: 'Failed',
    icon: '✕',
    fg: 'var(--color-danger, var(--brand-primary))',
    bg: 'var(--color-surface-hover)',
    border: 'var(--color-border)',
  },
  cancelled: {
    label: 'Cancelled',
    icon: '⊘',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
  unusable: {
    label: 'Unusable',
    icon: '⚠︎',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
};

export const PLAN_STEP_STATUS_META: Record<PlanStepStatus, StatusMeta> = {
  pending: {
    label: 'Pending',
    icon: '○',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
  running: {
    label: 'Running',
    icon: '▶︎',
    fg: 'var(--brand-accent)',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  completed: {
    label: 'Completed',
    icon: '✓',
    fg: 'var(--color-success, var(--brand-accent))',
    bg: 'var(--brand-accent-soft)',
    border: 'var(--brand-accent-border)',
  },
  failed: {
    label: 'Failed',
    icon: '✕',
    fg: 'var(--color-danger, var(--brand-primary))',
    bg: 'var(--color-surface-hover)',
    border: 'var(--color-border)',
  },
  cancelled: {
    label: 'Cancelled',
    icon: '⊘',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
  timed_out: {
    label: 'Timed out',
    icon: '⌛',
    fg: 'var(--color-danger, var(--brand-primary))',
    bg: 'var(--color-surface-hover)',
    border: 'var(--color-border)',
  },
  skipped: {
    label: 'Skipped',
    icon: '↷',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
  unusable: {
    label: 'Unusable',
    icon: '⚠︎',
    fg: 'var(--color-text-subtle)',
    bg: 'var(--color-surface)',
    border: 'var(--color-border)',
  },
};

export function formatElapsed(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '0.0s';
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const totalSec = Math.floor(ms / 1000);
  const mins = Math.floor(totalSec / 60);
  const secs = totalSec % 60;
  return `${mins}:${secs.toString().padStart(2, '0')}`;
}

export function progressCounts(steps: readonly { status: string }[]): {
  total: number;
  completed: number;
  running: number;
  failed: number;
  pending: number;
  percent: number;
} {
  let completed = 0;
  let running = 0;
  let failed = 0;
  let pending = 0;
  for (const step of steps) {
    if (step.status === 'completed' || step.status === 'skipped') completed++;
    else if (step.status === 'running') running++;
    else if (step.status === 'failed' || step.status === 'timed_out' || step.status === 'unusable' || step.status === 'cancelled') failed++;
    else pending++;
  }
  const total = steps.length;
  const finished = completed + failed;
  const percent = total === 0 ? 0 : Math.round((finished / total) * 100);
  return { total, completed, running, failed, pending, percent };
}
