/**
 * Single source of truth for the curated retail prompt library.
 *
 * Both the empty New Chat welcome state and the always-available prompt-library
 * popover consume these definitions, so the prompt text and categories are never
 * duplicated. Update prompts here and every surface stays in sync.
 *
 * Issue #109 introduces a richer per-task shape (display `name` vs submitted
 * `prompt`, optional `capability` metadata) sourced from the active content
 * pack. This module retains the built-in defaults as a synchronous fallback
 * so the welcome grid paints on first render before `/api/pack/starting-tasks`
 * resolves, and to keep the existing prompt-acceptance sweep grounded in a
 * stable set of prompt strings. Each fallback task's `name` is intentionally
 * set equal to its `prompt` so the manifest and DOM contracts (which match on
 * verbatim prompt text) continue to hold.
 */

/**
 * Declarative capability metadata attached to a starting task. Purely
 * descriptive — never changes execution behavior. Consumed by the UI to
 * project `data-capability-*` attributes on the button.
 */
export interface StartingTaskCapability {
  readonly kind: 'prose' | 'chart' | 'plan';
  readonly chartType?: string;
  readonly planPath?: string;
}

/**
 * One curated starting task in a category. Distinguishes the short display
 * `name` shown to the user from the fully-formed `prompt` string actually
 * submitted to the chat backend.
 */
export interface StartingTask {
  /** Short display label shown on the suggestion button. */
  readonly name: string;
  /** Verbatim prompt text submitted to the chat backend. */
  readonly prompt: string;
  /**
   * Optional explicit ordering. Tasks with a lower `order` render first
   * within their category; ties break on source-array position.
   */
  readonly order?: number;
  /** Optional declarative capability the task showcases. */
  readonly capability?: StartingTaskCapability;
}

export interface PromptCategory {
  id: string;
  label: string;
  emoji: string;
  /**
   * Optional explicit ordering. Categories with a lower `order` render first.
   * Ties break on source-array position so packs that leave the field off
   * still render deterministically.
   */
  order?: number;
  /**
   * Structured tasks — the primary rendering surface introduced by issue #109.
   * `prompts` is derived from this list for backward compatibility with the
   * prompt-acceptance manifest.
   */
  tasks: readonly StartingTask[];
  /** Legacy list of submitted prompt strings; derived from `tasks`. */
  prompts: string[];
}

/**
 * Build a category where each display `name` equals its submitted `prompt`.
 * The synchronous fallback preserves the pre-#109 UI text exactly and keeps
 * the prompt-acceptance manifest and DOM sweep grounded in the same strings.
 */
function mirrorCategory(
  id: string,
  label: string,
  emoji: string,
  order: number,
  prompts: readonly (readonly [string, StartingTaskCapability])[],
): PromptCategory {
  const tasks: StartingTask[] = prompts.map(([prompt, capability]) => ({
    name: prompt,
    prompt,
    capability,
  }));
  return {
    id,
    label,
    emoji,
    order,
    tasks,
    prompts: tasks.map((t) => t.prompt),
  };
}

const PROSE: StartingTaskCapability = { kind: 'prose' };
const CHART = (chartType: string): StartingTaskCapability => ({ kind: 'chart', chartType });

export const PROMPT_CATEGORIES: ReadonlyArray<PromptCategory> = [
  mirrorCategory('general', 'General Retail', '📊', 1, [
    ['Compare depletion trends across all regions for this quarter', PROSE],
    ['Which brands are growing fastest year-over-year across the portfolio?', PROSE],
    ['Show me field sentiment for our top 3 brands in the Southeast', PROSE],
  ]),
  mirrorCategory('grocery', 'Grocery', '🛒', 2, [
    ['How are FreshMart depletions trending in the Northeast this quarter?', PROSE],
    ['Compare Harvest Table vs FreshMart sell-through rates by region', PROSE],
    ['What is the field sentiment for Harvest Table Meal Kits in the Midwest?', PROSE],
  ]),
  mirrorCategory('qsr', 'Quick-Serve Restaurants', '🍔', 3, [
    ['How is Apex Grill performing in the Southwest this quarter?', PROSE],
    ['Compare Coastline Tacos vs Apex Grill depletions across all regions', CHART('groupedBar')],
    ['What is the field sentiment for Coastline Tacos in the West Coast?', PROSE],
  ]),
  mirrorCategory('home-improvement', 'Home Improvement', '🏠', 4, [
    ['Show me Pinnacle Hardware depletion stats in the Midwest for Q1', PROSE],
    ['How is Summit Outdoor performing in the Southeast vs West Coast?', PROSE],
    ['What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?', PROSE],
  ]),
  mirrorCategory('office-supply', 'Office Supply', '📎', 5, [
    ['How are ClearDesk depletions trending in the Northeast this quarter?', PROSE],
    ['Compare ClearDesk Technology vs Paper Products sell-through by region', PROSE],
    ['What is the field sentiment for ClearDesk in the Southeast?', PROSE],
  ]),
  mirrorCategory('furniture', 'Furniture', '🛋️', 6, [
    ['Show me Urban Living depletion trends across all regions this quarter', PROSE],
    ['Compare Foundry Home vs Urban Living performance in the West Coast', PROSE],
    ['What is the field sentiment for Urban Living in the Pacific Northwest?', PROSE],
  ]),
  mirrorCategory('charts', 'Charts', '📈', 7, [
    ['Create a line chart showing Sierra Gold Tequila depletion trends across all regions', CHART('line')],
    ['Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast', CHART('bar')],
    ['Create a pie chart showing market share breakdown for our grocery brands nationally', CHART('pie')],
    ['Show a grouped bar chart comparing FreshMart and Harvest Table across all regions', CHART('groupedBar')],
    ['Create a donut chart of Apex Grill variant mix in the Southwest', CHART('donut')],
    ['Show a horizontal bar chart ranking all brands by depletion growth rate', CHART('horizontalBar')],
    ['Create a table showing depletion stats for all home improvement brands by region', CHART('table')],
    ['Show a gauge chart for Pinnacle Hardware inventory health in the Midwest', CHART('gauge')],
  ]),
];
