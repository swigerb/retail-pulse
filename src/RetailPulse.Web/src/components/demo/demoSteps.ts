/**
 * Declarative script for the guided Demo Mode walkthrough.
 *
 * Every claim here is deliberately grounded in shipped behaviour, the gateway policy
 * really does meter tokens, the telemetry drawer really does show per-request cost, the
 * council really does convene specialists. A guided tour that overstates the product is
 * worse than none, because the person following it will click the thing and see the gap.
 */

/** Views the tour can drive the dashboard to. Mirrors `ActiveView` in Dashboard.tsx. */
export type DemoView =
  | 'chat' | 'promo' | 'competitive' | 'knowledge' | 'council' | 'security'
  | 'cards' | 'observability' | 'stores' | 'financials' | 'portfolio';

export interface DemoStep {
  readonly id: string;
  readonly title: string;
  /** Body copy. Kept to a few sentences, this is a tooltip, not documentation. */
  readonly body: string;
  /**
   * CSS selector for the element to spotlight. When absent, or when the element is not
   * on screen, the step renders centred with no cutout rather than pointing at nothing.
   */
  readonly target?: string;
  /** View to switch to before the step is shown. */
  readonly view?: DemoView;
  /** Whether the telemetry drawer should be open for this step. */
  readonly telemetry?: boolean;
  /** Preferred flyout placement; the tour flips it when it would leave the viewport. */
  readonly placement?: 'top' | 'bottom' | 'left' | 'right' | 'center';
  /** Short label shown in the progress rail. */
  readonly chapter: string;
}

export const DEMO_STEPS: readonly DemoStep[] = [
  {
    id: 'welcome',
    chapter: 'Welcome',
    title: 'Welcome to Retail Pulse',
    body:
      'A multi-agent retail intelligence platform. A router picks the right specialist for each '
      + 'question, specialists call real tools over MCP, and every model call is metered through an '
      + 'Azure API Management AI Gateway. This tour walks the whole surface, about two minutes.',
    view: 'chat',
    placement: 'center',
  },
  {
    id: 'ask',
    chapter: 'Chat',
    title: 'Ask in plain language',
    body:
      'Questions route to a specialist automatically, demand forecasting, competitive intelligence, '
      + 'supply chain, margin analysis, store operations and more. The curated prompts below are a '
      + 'starting point; anything in natural language works.',
    target: '[data-testid="chat-host"]',
    view: 'chat',
    placement: 'top',
  },
  {
    id: 'execution-path',
    chapter: 'Chat',
    title: 'Auto, Fast and Plan',
    body:
      'Auto lets the router decide. Fast forces a single-agent answer for latency. Plan runs '
      + 'plan-first orchestration: the agent drafts a multi-step plan, executes it step by step, and '
      + 'can pause for human approval before continuing.',
    target: '[aria-label="Execution path"]',
    view: 'chat',
    placement: 'top',
  },
  {
    id: 'telemetry-open',
    chapter: 'Cost & Telemetry',
    title: 'Real-time telemetry',
    body:
      'Every run streams over SignalR as it happens. Open this drawer to watch the agent work, '
      + 'no polling, no refresh.',
    target: '[aria-controls="telemetry-drawer"]',
    view: 'chat',
    placement: 'bottom',
  },
  {
    id: 'live-spans',
    chapter: 'Cost & Telemetry',
    title: 'Tokens, tool calls and cost per run',
    body:
      'Live Spans is the heart of this panel: total tokens, span count, tool calls, wall-clock '
      + 'duration and the dollar cost of the request you just made. Cost is derived from real token '
      + 'counts, not estimated.',
    target: '#telemetry-drawer',
    telemetry: true,
    placement: 'left',
  },
  {
    id: 'routing',
    chapter: 'Cost & Telemetry',
    title: 'Why that agent answered',
    body:
      'Agent Routing shows which specialist was selected and how confident the router was. Plans, '
      + 'Memory and Alerts sit alongside it, the whole execution story in one drawer.',
    target: '#telemetry-drawer',
    telemetry: true,
    placement: 'left',
  },
  {
    id: 'gateway',
    chapter: 'AI Gateway',
    title: 'Every model call goes through APIM',
    body:
      'No component talks to Azure OpenAI directly. An API Management AI Gateway fronts every '
      + 'inference call, authenticating to Azure AI Foundry with managed identity, there are no '
      + 'model keys anywhere in the system.',
    view: 'observability',
    placement: 'center',
  },
  {
    id: 'gateway-controls',
    chapter: 'AI Gateway',
    title: 'Token limits and emitted metrics',
    body:
      'The gateway policy applies a per-subscription token-per-minute limit with prompt-token '
      + 'estimation, returns tokens-consumed and retry-after headers, and emits token metrics '
      + 'dimensioned by API, operation and subscription. Throttling and attribution live in the '
      + 'gateway, not in application code.',
    view: 'observability',
    placement: 'center',
  },
  {
    id: 'cost-dashboard',
    chapter: 'AI Gateway',
    title: 'Cost tracking',
    body:
      'Total tokens, total cost, request count and average cost per request, by day, week or '
      + 'month, broken down per agent. This is what makes the economics of a multi-agent system '
      + 'visible rather than a surprise on the invoice.',
    view: 'observability',
    placement: 'center',
  },
  {
    id: 'council',
    chapter: 'Multi-agent',
    title: 'Portfolio Health Council',
    body:
      'Convene several specialists on one brand and watch them vote independently, each grounded '
      + 'in its own tool data, each reporting a confidence score. Disagreement is surfaced rather '
      + 'than averaged away, and the verdict is published as a collaborative card to vote on.',
    view: 'council',
    placement: 'center',
  },
  {
    id: 'portfolio',
    chapter: 'Multi-agent',
    title: 'Portfolio scorecard',
    body:
      'Each brand is scored across five specialist dimensions, demand, margin, competitive, supply '
      + 'and store execution, by fanning out to the specialists in parallel. Scores are cached so '
      + 'revisiting is instant.',
    view: 'portfolio',
    placement: 'center',
  },
  {
    id: 'knowledge',
    chapter: 'Grounding',
    title: 'Knowledge Base and RAG',
    body:
      'The corpus the agents ground answers in. Search it, upload to it, and see exactly which '
      + 'source each agent is bound to. Providers are pluggable, in-memory BM25 by default, with '
      + 'Azure AI Search and Foundry IQ available.',
    view: 'knowledge',
    placement: 'center',
  },
  {
    id: 'competitive',
    chapter: 'Analytics',
    title: 'Competitive intelligence',
    body:
      'Market share trends, competitor pricing moves and detected threats with recommended '
      + 'responses. Price drops here raise the alerts you saw in the telemetry drawer.',
    view: 'competitive',
    placement: 'center',
  },
  {
    id: 'financials',
    chapter: 'Analytics',
    title: 'Financials',
    body:
      'A P&L waterfall from revenue through to net margin, plus the drivers moving it, each one '
      + 'read live from the margin service, not a fixture.',
    view: 'financials',
    placement: 'center',
  },
  {
    id: 'stores',
    chapter: 'Analytics',
    title: 'Store operations',
    body:
      'Every store against target, with a regional heatmap and the SKUs at genuine stockout risk, '
      + 'ranked by urgency, with recommended reorder quantities.',
    view: 'stores',
    placement: 'center',
  },
  {
    id: 'promo',
    chapter: 'Planning',
    title: 'Campaign Planner',
    body:
      'Model a promotion before you run it: expected ROI with confidence bounds, break-even weeks, '
      + 'seasonality fit and the risks worth knowing, grounded in historical campaign outcomes.',
    view: 'promo',
    placement: 'center',
  },
  {
    id: 'cards',
    chapter: 'Collaboration',
    title: 'Adaptive Cards',
    body:
      'Council verdicts are published as interactive cards. Teammates vote, comment and escalate; a '
      + 'split vote escalates automatically. The same card format the Teams bot uses.',
    view: 'cards',
    placement: 'center',
  },
  {
    id: 'security',
    chapter: 'Trust',
    title: 'Guardrails and security',
    body:
      'Prompt-injection defence, PII redaction on both input and output, and Azure AI Content Safety '
      + 'scanning every prompt and response. Sign-in is Entra-only in production and every endpoint '
      + 'is deny-by-default.',
    view: 'security',
    placement: 'center',
  },
  {
    id: 'done',
    chapter: 'Done',
    title: "That's the tour",
    body:
      'Everything shown here runs against live services on Azure, no scripted responses. Ask it '
      + 'something of your own, or restart the tour any time from the Demo Mode button.',
    view: 'chat',
    placement: 'center',
  },
];
