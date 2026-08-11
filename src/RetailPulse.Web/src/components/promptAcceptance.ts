/**
 * Canonical acceptance manifest for EVERY curated Prompt Idea.
 *
 * The chart-only manifest (`chartAcceptance.ts`) covers the 9 chart-shaped
 * prompts and drives the real-Recharts render matrix. This module extends the
 * coverage to every prompt in `PROMPT_CATEGORIES` — including the 18 prose
 * prompts — with a single, uniform contract shape:
 *
 *   • id                    — stable synthetic id (`{category}:{index}`) so
 *                             tests can key off a symbol instead of quoting
 *                             prompt text everywhere.
 *   • categoryId            — the source category (`general`, `grocery`, `qsr`,
 *                             `home-improvement`, `office-supply`, `furniture`,
 *                             `charts`).
 *   • prompt                — the verbatim prompt text (single source of truth,
 *                             pulled from `PROMPT_CATEGORIES`).
 *   • responseClass         — `'chart'` for prompts that must yield a rendered
 *                             chart, `'prose'` for prompts that must yield a
 *                             narrated answer with entity mentions.
 *   • expectedVisualization — chart type for chart prompts, `'none'` for prose
 *                             prompts (explicit — never implicit).
 *   • expectedEntities      — entity labels (brands / regions / variants) the
 *                             response MUST reference so the demo lands.
 *   • expectedSeries        — chart prompts: minimum legend-bearing series.
 *                             prose prompts: `0` (not applicable).
 *   • expectedCategories    — chart prompts: minimum finite marks/rows.
 *                             prose prompts: minimum distinct entity mentions.
 *   • maxToolCalls          — hard ceiling on distinct tool invocations (matches
 *                             the backend #50 ceiling for chart prompts).
 *   • maxTokenBudget        — hard ceiling on cumulative tool-context tokens.
 *   • noJsonLeakage         — rendered assistant DOM must never contain a
 *                             chart-spec JSON block or raw tool payload.
 *   • noFallbackErrorText   — rendered DOM must never surface the
 *                             "Chart unavailable" diagnostic (only shown when
 *                             a spec has no bindable data).
 *
 * The prompt text is NEVER duplicated in this file — every case is derived
 * from `PROMPT_CATEGORIES` at module load, and the bidirectional drift test
 * (`promptAcceptance.contract.test.ts`) enforces that every featured prompt
 * has an acceptance case AND every acceptance case is still featured.
 */
import { PROMPT_CATEGORIES } from '../constants/prompts';
import { CHART_ACCEPTANCE_CASES, type ChartAcceptanceCase, type ChartType } from './chartAcceptance';

export type ResponseClass = 'chart' | 'prose';

export type ExpectedVisualization = ChartType | 'none';

export interface PromptAcceptanceCase {
  readonly id: string;
  readonly categoryId: string;
  readonly prompt: string;
  readonly responseClass: ResponseClass;
  readonly expectedVisualization: ExpectedVisualization;
  /** Entity labels (brands, regions, variants) that MUST appear in the response. */
  readonly expectedEntities: readonly string[];
  /** Minimum legend-bearing series (chart prompts) or 0 (prose prompts). */
  readonly expectedSeries: number;
  /** Minimum finite marks/rows (chart) or minimum entity mentions (prose). */
  readonly expectedCategories: number;
  /** Hard ceiling on distinct tool invocations — matches backend issue #50 ceiling. */
  readonly maxToolCalls: number;
  /** Hard ceiling on cumulative tool-context tokens — matches backend issue #50 ceiling. */
  readonly maxTokenBudget: number;
  /** True — assistant DOM must never contain a chart-spec JSON block. */
  readonly noJsonLeakage: true;
  /** True — assistant DOM must never surface the "Chart unavailable" fallback. */
  readonly noFallbackErrorText: true;
}

/**
 * Hard ceilings from issue #50 — chart prompts are the most tool-heavy demand
 * on the tool-context budget. Prose prompts are strictly lighter (single-tool
 * lookups, no chart fulfillment), so they share the same ceiling as a safety
 * ceiling that they can only under-run.
 */
const MAX_TOOL_CALLS = 5;
const MAX_TOKEN_BUDGET = 25_000;

/**
 * Prose-prompt semantics keyed by verbatim prompt text.
 *
 * A missing key trips the contract test — every non-chart featured prompt must
 * describe the entities its response is expected to name. This is the demo
 * bar: "field sentiment for Coastline Tacos in the West Coast" is only a
 * successful answer if the assistant references Coastline Tacos AND the West
 * Coast region; the same principle applies to every prose prompt.
 */
const PROSE_SEMANTICS: Record<
  string,
  { expectedEntities: readonly string[]; expectedCategories: number }
> = {
  // ── General Retail ──
  'Compare depletion trends across all regions for this quarter': {
    expectedEntities: ['depletion', 'region'],
    expectedCategories: 2,
  },
  'Which brands are growing fastest year-over-year across the portfolio?': {
    expectedEntities: ['brand', 'year-over-year'],
    expectedCategories: 2,
  },
  'Show me field sentiment for our top 3 brands in the Southeast': {
    expectedEntities: ['sentiment', 'Southeast'],
    expectedCategories: 2,
  },

  // ── Grocery ──
  'How are FreshMart depletions trending in the Northeast this quarter?': {
    expectedEntities: ['FreshMart', 'Northeast'],
    expectedCategories: 2,
  },
  'Compare Harvest Table vs FreshMart sell-through rates by region': {
    expectedEntities: ['Harvest Table', 'FreshMart'],
    expectedCategories: 2,
  },
  'What is the field sentiment for Harvest Table Meal Kits in the Midwest?': {
    expectedEntities: ['Harvest Table', 'Midwest'],
    expectedCategories: 2,
  },

  // ── Quick-Serve Restaurants ──
  'How is Apex Grill performing in the Southwest this quarter?': {
    expectedEntities: ['Apex Grill', 'Southwest'],
    expectedCategories: 2,
  },
  // NOTE: 'Compare Coastline Tacos vs Apex Grill depletions across all regions'
  // is a CHART prompt (grouped bar). It lives in CHART_ACCEPTANCE_CASES.
  'What is the field sentiment for Coastline Tacos in the West Coast?': {
    expectedEntities: ['Coastline Tacos', 'West Coast'],
    expectedCategories: 2,
  },

  // ── Home Improvement ──
  'Show me Pinnacle Hardware depletion stats in the Midwest for Q1': {
    expectedEntities: ['Pinnacle Hardware', 'Midwest'],
    expectedCategories: 2,
  },
  'How is Summit Outdoor performing in the Southeast vs West Coast?': {
    expectedEntities: ['Summit Outdoor', 'Southeast', 'West Coast'],
    expectedCategories: 3,
  },
  'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?': {
    expectedEntities: ['Pinnacle Hardware', 'Southwest'],
    expectedCategories: 2,
  },

  // ── Office Supply ──
  'How are ClearDesk depletions trending in the Northeast this quarter?': {
    expectedEntities: ['ClearDesk', 'Northeast'],
    expectedCategories: 2,
  },
  'Compare ClearDesk Technology vs Paper Products sell-through by region': {
    expectedEntities: ['ClearDesk', 'Technology', 'Paper Products'],
    expectedCategories: 2,
  },
  'What is the field sentiment for ClearDesk in the Southeast?': {
    expectedEntities: ['ClearDesk', 'Southeast'],
    expectedCategories: 2,
  },

  // ── Furniture ──
  'Show me Urban Living depletion trends across all regions this quarter': {
    expectedEntities: ['Urban Living', 'region'],
    expectedCategories: 2,
  },
  'Compare Foundry Home vs Urban Living performance in the West Coast': {
    expectedEntities: ['Foundry Home', 'Urban Living', 'West Coast'],
    expectedCategories: 3,
  },
  'What is the field sentiment for Urban Living in the Pacific Northwest?': {
    expectedEntities: ['Urban Living', 'Pacific Northwest'],
    expectedCategories: 2,
  },
};

/** Index chart cases by prompt text for O(1) lookup during flattening. */
const CHART_CASES_BY_PROMPT: ReadonlyMap<string, ChartAcceptanceCase> = new Map(
  CHART_ACCEPTANCE_CASES.map((c) => [c.prompt, c]),
);

/** Build one case per featured prompt, in category+source order. */
function buildManifest(): readonly PromptAcceptanceCase[] {
  const out: PromptAcceptanceCase[] = [];
  for (const cat of PROMPT_CATEGORIES) {
    cat.prompts.forEach((prompt, i) => {
      const id = `${cat.id}:${i}`;
      const chart = CHART_CASES_BY_PROMPT.get(prompt);
      if (chart) {
        out.push({
          id,
          categoryId: cat.id,
          prompt,
          responseClass: 'chart',
          expectedVisualization: chart.chartType,
          expectedEntities: chart.requiredEntities,
          expectedSeries: chart.minSeries,
          expectedCategories: chart.minMarks,
          maxToolCalls: MAX_TOOL_CALLS,
          maxTokenBudget: MAX_TOKEN_BUDGET,
          noJsonLeakage: true,
          noFallbackErrorText: true,
        });
        return;
      }
      const prose = PROSE_SEMANTICS[prompt];
      if (!prose) {
        throw new Error(
          `promptAcceptance: no semantics defined for featured prompt '${prompt}' — ` +
            'either add it to PROSE_SEMANTICS (prose) or CHART_ACCEPTANCE_CASES (chart).',
        );
      }
      out.push({
        id,
        categoryId: cat.id,
        prompt,
        responseClass: 'prose',
        expectedVisualization: 'none',
        expectedEntities: prose.expectedEntities,
        expectedSeries: 0,
        expectedCategories: prose.expectedCategories,
        maxToolCalls: MAX_TOOL_CALLS,
        maxTokenBudget: MAX_TOKEN_BUDGET,
        noJsonLeakage: true,
        noFallbackErrorText: true,
      });
    });
  }
  return out;
}

export const PROMPT_ACCEPTANCE_CASES: readonly PromptAcceptanceCase[] = buildManifest();

/** All featured prompt texts, in source order (mirror check target). */
export const FEATURED_PROMPTS: readonly string[] = PROMPT_CATEGORIES.flatMap(
  (c) => c.prompts,
);

/** Case lookup by verbatim prompt text. */
export function findAcceptanceCase(prompt: string): PromptAcceptanceCase | undefined {
  return PROMPT_ACCEPTANCE_CASES.find((c) => c.prompt === prompt);
}
