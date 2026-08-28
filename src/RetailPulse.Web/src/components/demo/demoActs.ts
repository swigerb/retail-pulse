import type { DemoView } from './demoSteps';

/**
 * Script for the automated Demo Mode run.
 *
 * Unlike the Tour — which narrates a static surface and waits for a click — this drives
 * the product: it submits real prompts through the same path a person typing would take,
 * waits for the answer to actually arrive, and moves through the views while the results
 * are on screen.
 *
 * Nothing here is faked. `prompt` steps hit the live API, so the tokens, cost and latency
 * shown in the telemetry drawer during the run are the real numbers for that request.
 */

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
   * A prompt to submit for real. The act does not advance until the response lands, so
   * the narration never runs ahead of the system.
   */
  readonly prompt?: string;
  /**
   * How long to hold this act once its work is done, in milliseconds. Long enough to read
   * the card and look at the result; short enough that the run keeps moving.
   */
  readonly holdMs?: number;
}

const READ = 7_000;
const GLANCE = 5_000;

export const DEMO_ACTS: readonly DemoAct[] = [
  {
    id: 'intro',
    chapter: 'Live demo',
    title: 'This is the real system',
    body:
      'Everything from here runs live against Azure — real prompts, real agents, real tool '
      + 'calls, real token costs. Nothing is scripted or pre-recorded. Sit back; it drives '
      + 'itself, and you can stop at any point.',
    view: 'chat',
    holdMs: GLANCE,
  },
  {
    id: 'ask-demand',
    chapter: 'Routing',
    title: 'Asking a demand question',
    body:
      'Submitting a real question now. Watch the router pick the demand-forecasting '
      + 'specialist, call its tools, and answer from live data — the whole turn is streaming '
      + 'into the telemetry drawer as it happens.',
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
      + 'span count, tool calls, wall-clock duration and dollar cost. Every one of those model '
      + 'calls went through the API Management AI Gateway, which meters tokens per '
      + 'subscription and authenticates to Foundry with managed identity — no model keys exist '
      + 'anywhere in the system.',
    telemetry: true,
    holdMs: READ + 2_000,
  },
  {
    id: 'ask-chart',
    chapter: 'Charts',
    title: 'Asking for a visualisation',
    body:
      'The same pipeline builds charts. This one is drawn deterministically from the tool '
      + 'payload rather than improvised by the model, so the numbers on the axis are the '
      + 'numbers the tools returned.',
    view: 'chat',
    telemetry: true,
    prompt: 'Show a horizontal bar chart ranking all brands by depletion growth rate',
    holdMs: READ + 2_000,
  },
  {
    id: 'council',
    chapter: 'Multi-agent',
    title: 'Convening the Health Council',
    body:
      'Several specialists assess one brand independently, each grounded in its own tool '
      + 'data and reporting its own confidence. Disagreement is surfaced rather than averaged '
      + 'away — and the verdict is published as a collaborative card to vote on.',
    view: 'council',
    telemetry: false,
    holdMs: READ,
  },
  {
    id: 'portfolio',
    chapter: 'Multi-agent',
    title: 'Scoring the whole portfolio',
    body:
      'Every brand scored across five specialist dimensions — demand, margin, competitive, '
      + 'supply and store execution — by fanning out to the specialists in parallel. Results '
      + 'are cached, so this is instant on a second visit.',
    view: 'portfolio',
    holdMs: READ,
  },
  {
    id: 'competitive',
    chapter: 'Analytics',
    title: 'Competitive intelligence',
    body:
      'Market share, competitor pricing moves and detected threats with recommended '
      + 'responses. The price drops here are what raise the alerts in the telemetry drawer.',
    view: 'competitive',
    holdMs: GLANCE,
  },
  {
    id: 'financials',
    chapter: 'Analytics',
    title: 'Financials',
    body:
      'A P&L waterfall from revenue to net margin with the drivers moving it — read live '
      + 'from the margin service.',
    view: 'financials',
    holdMs: GLANCE,
  },
  {
    id: 'stores',
    chapter: 'Analytics',
    title: 'Store operations',
    body:
      'Every store against target, a regional heatmap, and the SKUs at genuine stockout '
      + 'risk ranked by urgency with recommended reorder quantities.',
    view: 'stores',
    holdMs: GLANCE,
  },
  {
    id: 'knowledge',
    chapter: 'Grounding',
    title: 'What the agents read',
    body:
      'The corpus answers are grounded in, with per-agent bindings showing exactly which '
      + 'source each specialist is scoped to. Providers are pluggable — in-memory BM25 here, '
      + 'with Azure AI Search and Foundry IQ available.',
    view: 'knowledge',
    holdMs: GLANCE,
  },
  {
    id: 'promo',
    chapter: 'Planning',
    title: 'Campaign Planner',
    body:
      'Model a promotion before running it: expected ROI with confidence bounds, break-even '
      + 'weeks, seasonality fit and the risks worth knowing — grounded in historical outcomes.',
    view: 'promo',
    holdMs: GLANCE,
  },
  {
    id: 'cards',
    chapter: 'Collaboration',
    title: 'Adaptive Cards',
    body:
      'Council verdicts are published here as interactive cards. Teammates vote, comment and '
      + 'escalate; a split vote escalates automatically. The same format the Teams bot sends.',
    view: 'cards',
    holdMs: GLANCE,
  },
  {
    id: 'security',
    chapter: 'Trust',
    title: 'Guardrails',
    body:
      'Prompt-injection defence, PII redaction on input and output, and Azure AI Content '
      + 'Safety scanning every prompt and response. Entra-only sign-in, every endpoint '
      + 'deny-by-default.',
    view: 'security',
    holdMs: GLANCE,
  },
  {
    id: 'observability',
    chapter: 'AI Gateway',
    title: 'The bill',
    body:
      'Total tokens, total cost, request count and average cost per request — including the '
      + 'requests this demo just made. This is what makes the economics of a multi-agent '
      + 'system visible rather than a surprise on the invoice.',
    view: 'observability',
    holdMs: READ,
  },
  {
    id: 'outro',
    chapter: 'Done',
    title: 'All of that was live',
    body:
      'Every answer, chart and cost figure came from services running on Azure during this '
      + 'run. Ask it something of your own — or press Tour for the guided version you can '
      + 'step through at your own pace.',
    view: 'chat',
    holdMs: GLANCE,
  },
];
