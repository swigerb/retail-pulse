import { describe, it, expect } from 'vitest';
import {
  reconcilePlanSteps,
  applyReconciliation,
  isTerminalStatus,
} from '../services/planReconciler';
import type { PlanStepRecord } from '../services/executionControlApi';

function step(index: number, status: string, extra: Partial<PlanStepRecord> = {}): PlanStepRecord {
  return { stepIndex: index, status, ...extra };
}

describe('planReconciler.isTerminalStatus', () => {
  it('recognises terminal lifecycle statuses regardless of case', () => {
    expect(isTerminalStatus('succeeded')).toBe(true);
    expect(isTerminalStatus('Completed')).toBe(true);
    expect(isTerminalStatus('FAILED')).toBe(true);
    expect(isTerminalStatus('cancelled')).toBe(true);
    expect(isTerminalStatus('canceled')).toBe(true);
    expect(isTerminalStatus('skipped')).toBe(true);
  });

  it('treats in-flight statuses as non-terminal', () => {
    expect(isTerminalStatus('pending')).toBe(false);
    expect(isTerminalStatus('running')).toBe(false);
    expect(isTerminalStatus('waiting_approval')).toBe(false);
    expect(isTerminalStatus(null)).toBe(false);
    expect(isTerminalStatus(undefined)).toBe(false);
  });
});

describe('planReconciler.reconcilePlanSteps', () => {
  it('yields unique step entries sorted by stepIndex', () => {
    const rendered = [step(0, 'succeeded'), step(2, 'running')];
    const reconciled = [step(1, 'succeeded'), step(3, 'pending')];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged.map(s => s.stepIndex)).toEqual([0, 1, 2, 3]);
    // No duplicates.
    expect(new Set(merged.map(s => s.stepIndex)).size).toBe(merged.length);
  });

  it('never regresses a terminal rendered step (monotonicity)', () => {
    const rendered = [step(0, 'succeeded', { result: 'rendered-final' })];
    const reconciled = [step(0, 'running', { result: 'late-arrival' })];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged).toHaveLength(1);
    expect(merged[0].status).toBe('succeeded');
    expect(merged[0].result).toBe('rendered-final');
  });

  it('promotes an incoming terminal record over a non-terminal rendered one', () => {
    const rendered = [step(0, 'running', { result: 'stream-placeholder' })];
    const reconciled = [step(0, 'succeeded', { result: 'durable-final', durationMs: 1200 })];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged).toHaveLength(1);
    expect(merged[0].status).toBe('succeeded');
    expect(merged[0].result).toBe('durable-final');
    expect(merged[0].durationMs).toBe(1200);
  });

  it('produces no duplicates when overlap is a subset (rendered ⊇ reconciled)', () => {
    const rendered = [step(0, 'succeeded'), step(1, 'succeeded'), step(2, 'running')];
    const reconciled = [step(1, 'succeeded'), step(2, 'succeeded')];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged.map(s => s.stepIndex)).toEqual([0, 1, 2]);
    // The overlapping non-terminal (index 2) was promoted to succeeded.
    expect(merged.find(s => s.stepIndex === 2)?.status).toBe('succeeded');
  });

  it('leaves a gap when the reconcile response omits an intermediate step', () => {
    // Simulates streaming still delivering step 1 while reconcile has only
    // returned steps 0 and 2 so far. The caller relies on the gap to know
    // MORE data is incoming, so we must not paper over it.
    const rendered = [step(0, 'succeeded')];
    const reconciled = [step(2, 'succeeded')];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged.map(s => s.stepIndex)).toEqual([0, 2]);
  });

  it('drops step records with a non-finite stepIndex', () => {
    const rendered = [step(0, 'succeeded')];
    const reconciled = [
      { stepIndex: Number.NaN, status: 'succeeded' } as PlanStepRecord,
      step(1, 'succeeded'),
    ];

    const merged = reconcilePlanSteps(rendered, reconciled);
    expect(merged.map(s => s.stepIndex)).toEqual([0, 1]);
  });
});

describe('planReconciler.applyReconciliation', () => {
  it('returns the next cursor as max(stepIndex) so the caller keeps advancing', () => {
    const rendered = [step(0, 'succeeded')];
    const reconciled = [step(1, 'succeeded'), step(4, 'running')];

    const result = applyReconciliation(rendered, reconciled);
    expect(result.steps.map(s => s.stepIndex)).toEqual([0, 1, 4]);
    expect(result.nextAfterStepIndex).toBe(4);
  });

  it('returns -1 when nothing has been rendered yet', () => {
    const result = applyReconciliation<PlanStepRecord>([], []);
    expect(result.steps).toEqual([]);
    expect(result.nextAfterStepIndex).toBe(-1);
  });
});
