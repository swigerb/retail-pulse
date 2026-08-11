import { describe, it, expect } from 'vitest';
import { PROMPT_ACCEPTANCE_CASES } from '../components/promptAcceptance';

/**
 * Non-chart (prose) prompt acceptance contract.
 *
 * The chart matrix (`chartAcceptance.matrix.test.tsx`) covers the 9 chart
 * prompts against real Recharts. Prose prompts don't render a chart, but the
 * demo still contracts a shape:
 *
 *   • Every prose case declares the entities its response must reference.
 *   • Every prose case shares the same hard performance ceilings as the chart
 *     matrix (≤5 tool calls, <25K cumulative tool-context tokens) — a prose
 *     lookup must NEVER cost more than a chart fulfillment.
 *   • Every prose case explicitly promises no JSON leakage and no fallback
 *     error text in the rendered assistant DOM (asserted by the render suite
 *     and by `sanitizeMessage` — this test locks the promise into the
 *     manifest itself so a future edit can't quietly relax it).
 *
 * Coupled with the bidirectional drift test, this contract guarantees that
 * every non-chart prompt in the popover / welcome / library / README has a
 * defined behavior; no orphan prompts, no silently-broken ceilings.
 */
describe('Prompt Ideas — non-chart (prose) contract', () => {
  const proseCases = PROMPT_ACCEPTANCE_CASES.filter((c) => c.responseClass === 'prose');

  it('covers every non-chart prompt with at least one expected entity mention', () => {
    // 26 total prompts, 9 chart cases (8 chart-category + QSR mirror) → 17 prose.
    expect(proseCases.length).toBe(17);
    for (const c of proseCases) {
      expect(c.expectedEntities.length, `${c.prompt} — must declare ≥1 expected entity`).toBeGreaterThanOrEqual(1);
      expect(c.expectedCategories, `${c.prompt} — must declare ≥1 expected mention`).toBeGreaterThanOrEqual(1);
    }
  });

  it('every non-chart entity string is non-empty and free of markup', () => {
    for (const c of proseCases) {
      for (const entity of c.expectedEntities) {
        expect(entity.trim(), `${c.prompt} — empty entity`).not.toBe('');
        expect(entity, `${c.prompt} — entity '${entity}' must not contain markup`).not.toMatch(/[<>]/);
      }
    }
  });

  it('every non-chart prompt honors the hard tool-call and token-budget ceilings', () => {
    for (const c of proseCases) {
      expect(c.maxToolCalls, `${c.prompt} — tool-call ceiling`).toBeLessThanOrEqual(5);
      expect(c.maxTokenBudget, `${c.prompt} — token budget`).toBeLessThanOrEqual(25_000);
    }
  });

  it('every non-chart prompt explicitly promises no JSON leakage and no fallback error text', () => {
    for (const c of proseCases) {
      expect(c.noJsonLeakage, `${c.prompt} — noJsonLeakage`).toBe(true);
      expect(c.noFallbackErrorText, `${c.prompt} — noFallbackErrorText`).toBe(true);
      // Prose prompts never render a chart, so expectedVisualization must be explicit 'none'.
      expect(c.expectedVisualization).toBe('none');
    }
  });

  it('every chart prompt also honors the ceilings (chart cases mirror the same contract)', () => {
    const chartCases = PROMPT_ACCEPTANCE_CASES.filter((c) => c.responseClass === 'chart');
    expect(chartCases.length).toBe(9);
    for (const c of chartCases) {
      expect(c.maxToolCalls).toBeLessThanOrEqual(5);
      expect(c.maxTokenBudget).toBeLessThanOrEqual(25_000);
      expect(c.noJsonLeakage).toBe(true);
      expect(c.noFallbackErrorText).toBe(true);
      expect(c.expectedVisualization).not.toBe('none');
    }
  });
});
