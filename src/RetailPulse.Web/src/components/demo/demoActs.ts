import type { DemoView } from './demoSteps';

/**
 * Script for the automated Demo Mode run.
 *
 * Unlike the Tour, which narrates a static surface and waits for a click, this drives the
 * product. It submits real prompts through the same path a person typing would take, runs
 * real interactions inside each panel, and waits for the work to finish before narrating
 * the result.
 *
 * Nothing here is faked. Prompts hit the live API and interactions click the same controls
 * an operator would, so the tokens, cost and latency shown during a run are that run's
 * real numbers.
 */

/**
 * One scripted interaction inside a panel.
 *
 * Declarative rather than a callback, so the script stays inspectable and testable and a
 * missing target degrades to a skipped step instead of throwing part way through a demo.
 */
export type DemoInteraction =
  | { readonly kind: 'click'; readonly selector: string; readonly note: string }
  | { readonly kind: 'type'; readonly selector: string; readonly text: string; readonly note: string }
  | { readonly kind: 'scroll'; readonly selector: string; readonly note: string }
  | { readonly kind: 'wait'; readonly ms: number; readonly note: string };

export interface DemoAct {
  readonly id: string;
  readonly chapter: string;
  readonly title: string;
  readonly body: string;
  /** View to switch to before the act runs. */
  readonly view?: DemoView;
  /** Whether the telemetry drawer should be open. */
  readonly telemetry?: boolean;
  /**
   * A prompt to submit for real. The act does not advance until the response lands, so the
   * narration never runs ahead of the system.
   */
  readonly prompt?: string;
  /** Controls to drive inside the panel once it is on screen. */
  readonly interactions?: readonly DemoInteraction[];
  /** How long to hold once the work is done, so the result can be read. */
  readonly holdMs?: number;
}

const READ = 8_000;
const GLANCE = 6_000;

export const DEMO_ACTS: readonly DemoAct[] = [
  {
    id: 'intro',
    chapter: 'Live demo',
    title: 'This is the real system',
    body:
      'Everything from here runs live against Azure. Real prompts, real agents, real tool '
      + 'calls, real token costs. Nothing is scripted or pre-recorded. It drives itself, and '
      + 'you can pause or step back at any point.',
    view: 'chat',
    telemetry: false,
    holdMs: GLANCE,
  },
  {
    id: 'ask-demand',
    chapter: 'Routing',
    title: 'Asking a demand question',
    body:
      'Submitting a real question now. The router picks the demand specialist, it calls its '
      + 'tools, and the answer comes back from live data. The whole turn streams into the '
      + 'telemetry drawer as it happens.',
    view: 'chat',
    telemetry: true,
    prompt: 'How are FreshMart depletions trending in the Northeast this quarter?',
    holdMs: READ,
  },
  {
    id: 'cost',
    chapter: 'AI Gateway',
    title: 'What that answer cost',
    body:
      'Live Spans now holds the real numbers for the request you just watched: total tokens, '
      + 'span count, tool calls, duration and dollar cost. Every model call went through the '
      + 'API Management AI Gateway, which meters tokens per subscription and authenticates to '
      + 'Foundry with managed identity. No model keys exist anywhere in the system.',
    telemetry: true,
    holdMs: READ + 2_000,
  },
  {
    id: 'ask-chart',
    chapter: 'Charts',
    title: 'Asking for a visualisation',
    body:
      'The same pipeline builds charts. This one is drawn deterministically from the tool '
      + 'payload rather than improvised by the model, so the values on the axis are exactly '
      + 'what the tools returned.',
    view: 'chat',
    telemetry: true,
    prompt: 'Show a horizontal bar chart ranking all brands by depletion growth rate',
    interactions: [
      // A long answer pushes its chart below the fold, so bring it into view before the
      // narration starts describing it.
      { kind: 'scroll', selector: '[data-testid="chart-card"]', note: 'Scrolling to the chart' },
    ],
    holdMs: READ + 2_000,
  },
  {
    id: 'council',
    chapter: 'Multi-agent',
    title: 'Convening the Health Council',
    body:
      'Clicking Convene now. Several specialists assess one brand independently, each '
      + 'grounded in its own tool data and reporting its own confidence. Watch where they '
      + 'disagree: the split is surfaced rather than averaged away.',
    view: 'council',
    telemetry: false,
    interactions: [
      { kind: 'click', selector: '[data-testid="convene-button"]', note: 'Convening the council' },
      { kind: 'wait', ms: 25_000, note: 'Specialists are voting' },
    ],
    holdMs: READ + 4_000,
  },
  {
    id: 'portfolio',
    chapter: 'Multi-agent',
    title: 'Scoring the whole portfolio',
    body:
      'Every brand scored across five specialist dimensions: demand, margin, competitive, '
      + 'supply and store execution. The specialists run in parallel and the scores are '
      + 'cached, so a second visit is instant.',
    view: 'portfolio',
    interactions: [
      { kind: 'wait', ms: 14_000, note: 'Scoring brands' },
    ],
    holdMs: READ,
  },
  {
    id: 'competitive',
    chapter: 'Analytics',
    title: 'Filtering competitive intelligence',
    body:
      'Market share, competitor pricing moves and detected threats with recommended '
      + 'responses. Opening the category filter now. The list is derived from the data, so '
      + 'every option returns rows rather than an empty grid.',
    view: 'competitive',
    interactions: [
      { kind: 'wait', ms: 3_500, note: 'Loading competitive data' },
      { kind: 'click', selector: '[data-testid="category-filter"] button', note: 'Opening the category filter' },
      { kind: 'wait', ms: 1_200, note: '' },
      { kind: 'click', selector: '[role="option"]', note: 'Choosing a category' },
      { kind: 'wait', ms: 3_000, note: 'Refiltering' },
    ],
    holdMs: READ,
  },
  {
    id: 'financials',
    chapter: 'Analytics',
    title: 'Financials',
    body:
      'A P and L waterfall from revenue through to net margin, with the drivers moving it. '
      + 'Switching to another brand now: every figure is read live from the margin service '
      + 'rather than a fixture, so the whole panel redraws against that brand\u2019s book.',
    view: 'financials',
    interactions: [
      { kind: 'wait', ms: 3_000, note: 'Loading margin data' },
      { kind: 'click', selector: '[data-testid="financials-brand-filter"] button', note: 'Opening the brand picker' },
      { kind: 'wait', ms: 1_200, note: '' },
      // The picker opens with the current brand selected, so take the first option that
      // is not, which is guaranteed to actually change the panel.
      { kind: 'click', selector: '[role="option"][aria-selected="false"]', note: 'Choosing another brand' },
      { kind: 'wait', ms: 3_500, note: 'Recalculating the P and L' },
    ],
    holdMs: READ,
  },
  {
    id: 'stores',
    chapter: 'Analytics',
    title: 'Store operations',
    body:
      'Every store against target, a regional heatmap, and the SKUs at genuine stockout risk '
      + 'ranked by urgency with recommended reorder quantities.',
    view: 'stores',
    interactions: [
      { kind: 'wait', ms: 3_000, note: 'Loading store performance' },
    ],
    holdMs: GLANCE,
  },
  {
    id: 'knowledge',
    chapter: 'Grounding',
    title: 'Searching what the agents read',
    body:
      'This is the corpus answers are grounded in. Running a real search now. The results '
      + 'carry BM25 relevance, and the panel on the right shows exactly which source each '
      + 'specialist is scoped to.',
    view: 'knowledge',
    interactions: [
      { kind: 'wait', ms: 2_500, note: 'Loading the corpus' },
      { kind: 'type', selector: '[data-testid="kb-search-input"]', text: 'supplier fill rate service level', note: 'Typing a search' },
      { kind: 'wait', ms: 800, note: '' },
      { kind: 'click', selector: '[data-testid="kb-search-button"]', note: 'Searching' },
      { kind: 'wait', ms: 3_000, note: 'Retrieving' },
    ],
    holdMs: READ,
  },
  {
    id: 'promo',
    chapter: 'Planning',
    title: 'Campaign Planner',
    body:
      'Model a promotion before running it. Expected ROI with confidence bounds, break even '
      + 'weeks, seasonality fit and the risks worth knowing, all grounded in historical '
      + 'campaign outcomes.',
    view: 'promo',
    interactions: [
      { kind: 'wait', ms: 2_500, note: 'Loading campaign history' },
    ],
    holdMs: GLANCE,
  },
  {
    id: 'cards',
    chapter: 'Collaboration',
    title: 'Adaptive Cards',
    body:
      'The council verdict from earlier was published here as an interactive card. '
      + 'Teammates vote, comment and escalate, and a split vote escalates automatically. This '
      + 'is the same card format the Teams bot sends.',
    view: 'cards',
    interactions: [
      { kind: 'wait', ms: 2_500, note: 'Loading cards' },
    ],
    holdMs: GLANCE,
  },
  {
    id: 'security',
    chapter: 'Trust',
    title: 'Guardrails',
    body:
      'Prompt injection defence, PII redaction on input and output, and Azure AI Content '
      + 'Safety scanning every prompt and response. Sign in is Entra only in production and '
      + 'every endpoint is deny by default.',
    view: 'security',
    holdMs: GLANCE,
  },
  {
    id: 'observability',
    chapter: 'AI Gateway',
    title: 'The bill',
    body:
      'Total tokens, total cost, request count and average cost per request, broken down per '
      + 'agent. That includes the requests this demo just made. This is what makes the '
      + 'economics of a multi-agent system visible rather than a surprise on the invoice.',
    view: 'observability',
    interactions: [
      { kind: 'wait', ms: 3_000, note: 'Loading cost data' },
    ],
    holdMs: READ,
  },
  {
    id: 'outro',
    chapter: 'Done',
    title: 'All of that was live',
    body:
      'Every answer, chart and cost figure came from services running on Azure during this '
      + 'run. Ask it something of your own, or press Tour for the guided version you can step '
      + 'through at your own pace.',
    view: 'chat',
    holdMs: GLANCE,
  },
];
