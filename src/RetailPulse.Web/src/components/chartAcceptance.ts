/**
 * Canonical chart-acceptance manifest (frontend mirror).
 *
 * The prompt TEXT is never duplicated here — it is derived from the single prompt
 * source `PROMPT_CATEGORIES` (constants/prompts.ts). This module only adds the
 * render semantics each curated chart prompt must satisfy (chart type, minimum
 * series/marks, required entity labels, unit/axis, whether the axis is a percentage).
 *
 * It is the frontend half of the cross-language acceptance contract: the backend
 * `RetailPulse.Contracts.Charts.ChartAcceptanceManifest` holds the same definitions,
 * and `chartAcceptance.contract.test.ts` + the backend contract test both assert the
 * two surfaces (and the README chart list) stay synchronized with this source.
 */
import { PROMPT_CATEGORIES } from '../constants/prompts';

export type ChartType =
  | 'line'
  | 'bar'
  | 'groupedBar'
  | 'stackedBar'
  | 'horizontalBar'
  | 'pie'
  | 'donut'
  | 'gauge'
  | 'table';

export type ChartDataSource =
  | 'HistoricalDemand'
  | 'PortfolioDepletion'
  | 'MarketShare'
  | 'VariantMix'
  | 'InventoryLevels'
  | 'DepletionStats';

export interface ChartAcceptanceCase {
  /** Verbatim curated prompt text (must exist in the single prompt source). */
  readonly prompt: string;
  readonly chartType: ChartType;
  /** Minimum legend-bearing series. */
  readonly minSeries: number;
  /** Minimum finite marks (bars / points / sectors / rows) across all series. */
  readonly minMarks: number;
  /** Entity labels that must appear as a legend or category. */
  readonly requiredEntities: readonly string[];
  /** Unit / axis semantics (documentation + assertion hint). */
  readonly axisUnit: string;
  readonly dataSource: ChartDataSource;
  /** True when Y values are percentages (share / growth / mix / gauge). */
  readonly percentAxis: boolean;
}

/** The curated "Charts" category prompts, in source order (single source of truth). */
const CHART_CATEGORY_PROMPTS: readonly string[] =
  PROMPT_CATEGORIES.find((c) => c.id === 'charts')?.prompts ?? [];

/** The previously-validated two-brand QSR comparison prompt (single source of truth). */
const TWO_BRAND_COMPARISON_PROMPT =
  PROMPT_CATEGORIES.find((c) => c.id === 'qsr')?.prompts.find((p) =>
    p.startsWith('Compare Coastline Tacos vs Apex Grill'),
  ) ?? '';

/**
 * Semantics per curated chart prompt, keyed by the exact prompt text so the manifest
 * cannot silently drift from the source array (a missing key fails the contract test).
 */
const SEMANTICS: Record<string, Omit<ChartAcceptanceCase, 'prompt'>> = {
  'Create a line chart showing Sierra Gold Tequila depletion trends across all regions': {
    chartType: 'line',
    minSeries: 1,
    minMarks: 2,
    requiredEntities: ['Sierra Gold Tequila'],
    axisUnit: 'Depletion Volume',
    dataSource: 'HistoricalDemand',
    percentAxis: false,
  },
  'Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast': {
    chartType: 'bar',
    minSeries: 1,
    minMarks: 3,
    requiredEntities: ['Sierra Gold Tequila', 'Ridgeline Bourbon', 'Summit Vodka'],
    axisUnit: 'Avg Weekly Depletion Velocity',
    dataSource: 'HistoricalDemand',
    percentAxis: false,
  },
  'Create a pie chart showing market share breakdown for our grocery brands nationally': {
    chartType: 'pie',
    minSeries: 1,
    minMarks: 2,
    requiredEntities: ['FreshMart', 'Harvest Table'],
    axisUnit: 'Market Share %',
    dataSource: 'MarketShare',
    percentAxis: true,
  },
  'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions': {
    chartType: 'groupedBar',
    minSeries: 2,
    minMarks: 12,
    requiredEntities: ['FreshMart', 'Harvest Table'],
    axisUnit: 'Depletion Volume',
    dataSource: 'HistoricalDemand',
    percentAxis: false,
  },
  'Create a donut chart of Apex Grill variant mix in the Southwest': {
    chartType: 'donut',
    minSeries: 1,
    minMarks: 2,
    requiredEntities: ['Apex Grill'],
    axisUnit: 'Variant Mix %',
    dataSource: 'VariantMix',
    percentAxis: true,
  },
  'Show a horizontal bar chart ranking all brands by depletion growth rate': {
    chartType: 'horizontalBar',
    minSeries: 1,
    minMarks: 6,
    requiredEntities: [],
    axisUnit: 'Depletion Growth Rate % (YoY)',
    dataSource: 'PortfolioDepletion',
    percentAxis: true,
  },
  'Create a table showing depletion stats for all home improvement brands by region': {
    chartType: 'table',
    minSeries: 1,
    minMarks: 2,
    requiredEntities: ['Pinnacle Hardware', 'Summit Outdoor'],
    axisUnit: 'Depletion Stats',
    dataSource: 'DepletionStats',
    percentAxis: false,
  },
  'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest': {
    chartType: 'gauge',
    minSeries: 1,
    minMarks: 1,
    requiredEntities: ['Pinnacle Hardware'],
    axisUnit: 'Inventory Health % (0–100)',
    dataSource: 'InventoryLevels',
    percentAxis: true,
  },
  'Compare Coastline Tacos vs Apex Grill depletions across all regions': {
    chartType: 'groupedBar',
    minSeries: 2,
    minMarks: 4,
    requiredEntities: ['Coastline Tacos', 'Apex Grill'],
    axisUnit: 'Depletion Volume',
    dataSource: 'HistoricalDemand',
    percentAxis: false,
  },
};

/** All acceptance cases (chart-category prompts + the two-brand comparison), in source order. */
export const CHART_ACCEPTANCE_CASES: readonly ChartAcceptanceCase[] = [
  ...CHART_CATEGORY_PROMPTS,
  ...(TWO_BRAND_COMPARISON_PROMPT ? [TWO_BRAND_COMPARISON_PROMPT] : []),
]
  .filter((prompt) => prompt in SEMANTICS)
  .map((prompt) => ({ prompt, ...SEMANTICS[prompt] }));

/** The curated chart prompt texts covered by the manifest. */
export const CHART_ACCEPTANCE_PROMPTS: readonly string[] = CHART_ACCEPTANCE_CASES.map(
  (c) => c.prompt,
);
