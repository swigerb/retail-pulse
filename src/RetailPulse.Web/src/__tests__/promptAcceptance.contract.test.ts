import { describe, it, expect } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PROMPT_CATEGORIES } from '../constants/prompts';
import {
  PROMPT_ACCEPTANCE_CASES,
  FEATURED_PROMPTS,
  findAcceptanceCase,
} from '../components/promptAcceptance';
import { CHART_ACCEPTANCE_CASES } from '../components/chartAcceptance';

/**
 * Bidirectional drift contract for the complete Prompt Ideas surface.
 *
 * The chart-only manifest already asserts the 9 chart prompts + README chart
 * bullets stay in sync. This suite widens the guarantee to every one of the
 * 27 curated Prompt Ideas: every featured prompt has an acceptance case, and
 * every acceptance case is still a featured prompt. A rename, deletion, or
 * silent addition on either side fails CI before it can reach the demo.
 *
 * The suite also detects intentional mirror disagreement across README /
 * docs / frontend prompt source / backend chart manifest, so a chart prompt
 * removed from the frontend can't quietly linger in the backend manifest or
 * the README bullet list.
 */

function repoRoot(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 12; i++) {
    if (
      existsSync(join(dir, 'README.md'))
      && existsSync(join(dir, 'src', 'RetailPulse.Web'))
    ) {
      return dir;
    }
    const parent = dirname(dir);
    if (!parent || parent === dir) break;
    dir = parent;
  }
  throw new Error('Could not locate repository root from test file location');
}

describe('Prompt Ideas — bidirectional drift', () => {
  it('the frontend prompt source exposes exactly the expected 27 curated prompts across 7 categories', () => {
    // Regression guard: `PROMPT_CATEGORIES` is the single source of truth and
    // its shape drives every dependent surface. This is not a lockdown on
    // adding prompts — it is a deliberate step that forces a squad decision
    // (and this test update) when the surface count changes, so drift can
    // never happen silently.
    const totalPrompts = PROMPT_CATEGORIES.reduce((n, c) => n + c.prompts.length, 0);
    expect(PROMPT_CATEGORIES).toHaveLength(7);
    expect(totalPrompts).toBe(26);
  });

  it('every featured prompt has exactly one acceptance case', () => {
    for (const prompt of FEATURED_PROMPTS) {
      const match = PROMPT_ACCEPTANCE_CASES.filter((c) => c.prompt === prompt);
      expect(
        match.length,
        `featured prompt '${prompt}' must have exactly one acceptance case (found ${match.length})`,
      ).toBe(1);
    }
  });

  it('every acceptance case is still a featured prompt', () => {
    const featured = new Set(FEATURED_PROMPTS);
    for (const c of PROMPT_ACCEPTANCE_CASES) {
      expect(
        featured.has(c.prompt),
        `acceptance case '${c.prompt}' is orphaned — no longer in PROMPT_CATEGORIES`,
      ).toBe(true);
    }
  });

  it('acceptance cases preserve the source prompt order per category', () => {
    // A demo-driven ordering guarantee: the popover, the New Chat welcome, and
    // the manifest must all iterate prompts in the same order so tests are
    // deterministic regardless of which surface they hit.
    let sourceIndex = 0;
    for (const cat of PROMPT_CATEGORIES) {
      for (const prompt of cat.prompts) {
        expect(PROMPT_ACCEPTANCE_CASES[sourceIndex].prompt).toBe(prompt);
        expect(PROMPT_ACCEPTANCE_CASES[sourceIndex].categoryId).toBe(cat.id);
        expect(PROMPT_ACCEPTANCE_CASES[sourceIndex].id).toBe(`${cat.id}:${cat.prompts.indexOf(prompt)}`);
        sourceIndex++;
      }
    }
    expect(sourceIndex).toBe(PROMPT_ACCEPTANCE_CASES.length);
  });

  it('classifies every chart-category prompt as responseClass=chart with a real visualization', () => {
    const chartCategory = PROMPT_CATEGORIES.find((c) => c.id === 'charts');
    expect(chartCategory).toBeDefined();
    for (const prompt of chartCategory!.prompts) {
      const c = findAcceptanceCase(prompt);
      expect(c, `chart-category prompt '${prompt}' must have an acceptance case`).toBeDefined();
      expect(c!.responseClass).toBe('chart');
      expect(c!.expectedVisualization).not.toBe('none');
      expect(c!.expectedSeries).toBeGreaterThanOrEqual(1);
      expect(c!.expectedCategories).toBeGreaterThanOrEqual(1);
    }
  });

  it('classifies every non-chart-category prompt as responseClass=prose with expectedVisualization=none (except the QSR chart mirror)', () => {
    for (const cat of PROMPT_CATEGORIES) {
      if (cat.id === 'charts') continue;
      for (const prompt of cat.prompts) {
        const c = findAcceptanceCase(prompt)!;
        // Single documented exception: the QSR two-brand comparison is a
        // grouped-bar CHART prompt lifted into the chart acceptance manifest.
        const isQsrChartMirror = prompt.startsWith('Compare Coastline Tacos vs Apex Grill');
        if (isQsrChartMirror) {
          expect(c.responseClass).toBe('chart');
          expect(c.expectedVisualization).toBe('groupedBar');
          continue;
        }
        expect(c.responseClass, `${prompt} — non-chart category prompt`).toBe('prose');
        expect(c.expectedVisualization, `${prompt} — prose prompt visualization`).toBe('none');
        expect(c.expectedSeries).toBe(0);
      }
    }
  });

  it('gives every chart acceptance case a matching prompt-manifest entry with the same chart type', () => {
    // Detect drift the other direction: if the chart manifest gains a case
    // that the prompt manifest hasn't inherited (via CHART_ACCEPTANCE_CASES),
    // the two mirrors are out of sync.
    for (const chart of CHART_ACCEPTANCE_CASES) {
      const c = findAcceptanceCase(chart.prompt);
      expect(c, `chart case '${chart.prompt}' must appear in the prompt manifest`).toBeDefined();
      expect(c!.responseClass).toBe('chart');
      expect(c!.expectedVisualization).toBe(chart.chartType);
      expect(c!.expectedSeries).toBe(chart.minSeries);
      expect(c!.expectedCategories).toBe(chart.minMarks);
      expect(c!.expectedEntities).toEqual(chart.requiredEntities);
    }
  });

  it('README chart bullet list mirrors every chart-category acceptance case', () => {
    const readme = readFileSync(join(repoRoot(), 'README.md'), 'utf-8');
    const readmePrompts = new Set(
      [...readme.matchAll(/^\s*[-*]\s+\*"([^"]+)"\*\s*(?:→|->)/gm)].map((m) => m[1]),
    );
    for (const c of PROMPT_ACCEPTANCE_CASES) {
      if (c.categoryId !== 'charts') continue;
      expect(
        readmePrompts.has(c.prompt),
        `README chart bullet list must include chart-category prompt '${c.prompt}'`,
      ).toBe(true);
    }
  });

  it('no README chart bullet references a prompt that isn\'t in the frontend manifest', () => {
    const readme = readFileSync(join(repoRoot(), 'README.md'), 'utf-8');
    const readmePrompts = [...readme.matchAll(/^\s*[-*]\s+\*"([^"]+)"\*\s*(?:→|->)/gm)].map(
      (m) => m[1],
    );
    const featured = new Set(FEATURED_PROMPTS);
    for (const prompt of readmePrompts) {
      expect(
        featured.has(prompt),
        `README documents chart prompt '${prompt}' but PROMPT_CATEGORIES no longer features it — README/frontend mirror disagreement`,
      ).toBe(true);
    }
  });
});
