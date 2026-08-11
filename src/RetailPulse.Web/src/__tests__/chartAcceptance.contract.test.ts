import { describe, it, expect } from 'vitest';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PROMPT_CATEGORIES } from '../constants/prompts';
import { CHART_ACCEPTANCE_CASES, CHART_ACCEPTANCE_PROMPTS } from '../components/chartAcceptance';

/**
 * Cross-language contract test for the curated chart acceptance manifest.
 *
 * The single source of truth for the curated prompt library is `constants/prompts.ts`.
 * The frontend acceptance manifest (`components/chartAcceptance.ts`) must cover exactly
 * the "Charts" category (in prompt-source order) plus the previously-validated two-brand
 * QSR comparison prompt. The README's user-facing "example prompts" bullet list and the
 * backend acceptance manifest (`RetailPulse.Contracts.Charts.ChartAcceptanceManifest`) are
 * verified against the same source in this test and a matching backend test — so a drift
 * in any of the three surfaces fails CI immediately, before it can reach a live browser.
 */

const CHARTS_CATEGORY_ID = 'charts';
const QSR_CATEGORY_ID = 'qsr';
const QSR_COMPARISON_PROMPT_PREFIX = 'Compare Coastline Tacos vs Apex Grill';

function repoRoot(): string {
  // Walk up until we find the repository root, distinguished from RetailPulse.Web's
  // own README.md by the presence of the top-level src/RetailPulse.Web directory.
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

describe('ChartAcceptance manifest contract', () => {
  it('mirrors the Charts category plus the QSR comparison prompt from the prompt source', () => {
    const chartCategory = PROMPT_CATEGORIES.find((c) => c.id === CHARTS_CATEGORY_ID);
    expect(chartCategory, "prompt source must define a 'charts' category").toBeDefined();

    const qsrCategory = PROMPT_CATEGORIES.find((c) => c.id === QSR_CATEGORY_ID);
    expect(qsrCategory, "prompt source must define a 'qsr' category").toBeDefined();

    const comparisonPrompt = qsrCategory!.prompts.find((p) =>
      p.startsWith(QSR_COMPARISON_PROMPT_PREFIX),
    );
    expect(
      comparisonPrompt,
      'the QSR two-brand comparison prompt must exist in the prompt source',
    ).toBeDefined();

    const expected = [...chartCategory!.prompts, comparisonPrompt!];
    expect(CHART_ACCEPTANCE_PROMPTS).toEqual(expected);
  });

  it('gives every acceptance case coherent semantics', () => {
    expect(CHART_ACCEPTANCE_CASES.length).toBeGreaterThan(0);
    for (const c of CHART_ACCEPTANCE_CASES) {
      expect(c.prompt).toBeTruthy();
      expect(c.chartType).toBeTruthy();
      expect(c.minSeries).toBeGreaterThanOrEqual(1);
      expect(c.minMarks).toBeGreaterThanOrEqual(1);
      expect(c.axisUnit).toBeTruthy();
      if (c.chartType === 'groupedBar' || c.chartType === 'stackedBar') {
        expect(
          c.minSeries,
          `${c.prompt} — grouped/stacked charts are by definition multi-series`,
        ).toBeGreaterThanOrEqual(2);
      }
    }
  });

  it('exposes every "Charts" prompt in the README chart bullet list', () => {
    const readme = readFileSync(join(repoRoot(), 'README.md'), 'utf-8');
    const readmePrompts = new Set(
      [...readme.matchAll(/^\s*[-*]\s+\*"([^"]+)"\*\s*(?:→|->)/gm)].map((m) => m[1]),
    );

    for (const prompt of CHART_ACCEPTANCE_PROMPTS) {
      if (prompt.startsWith(QSR_COMPARISON_PROMPT_PREFIX)) {
        // The QSR two-brand comparison is documented in a separate example block
        // (the comparison-chart P0 fix in issue #32) rather than the Charts bullet
        // list, so it's not expected in the Charts README bullets.
        continue;
      }
      expect(
        readmePrompts.has(prompt),
        `README chart bullet list must include the curated Charts prompt '${prompt}'`,
      ).toBe(true);
    }
  });
});
