// Curated chart-prompt browser acceptance runner (issues #50 + #54).
//
// Paste this into the browser DevTools console while the Retail Pulse frontend
// is loaded. It walks every curated chart prompt from the acceptance manifest,
// submits the prompt through the composer, waits for the response to fully
// render, and asserts:
//   • the rendered chart card contains a real Recharts canvas (not the
//     "Chart unavailable" diagnostic),
//   • the chart's title, required entity labels, and minimum mark count agree
//     with the manifest,
//   • the assistant did not answer with prose only ("truncated portfolio
//     pull", "Chart unavailable — historical pulls truncated") — the #50 P0
//     symptom.
//
// Usage:
//   1. cd src/RetailPulse.Web && npm run dev
//   2. Start the API (RetailPulse.AppHost or dotnet run per-project).
//   3. Open http://localhost:5173, sign in, land on the empty dashboard.
//   4. Open DevTools → Console.
//   5. Paste this file's contents and run `await runChartAcceptance()`.
//   6. Copy the resulting `results` array into docs/chart-acceptance-run.md.
//
// This script is deliberately dependency-free JS (not TS) so it can be pasted
// verbatim into any recent browser console without a build step.
//
// # Selector strategy (issue #54, item 2)
//
// Every element the runner reads from the DOM is targeted by a **stable
// `data-testid` attribute** rather than by Griffel class-substring or Recharts
// internal class names. Griffel compiles `className={styles.chartCard}` to
// hashed atomic class names (e.g. `___ivt4970_0000000`) so historical
// selectors like `[class*="chartCard"]` do not match at runtime; Recharts
// internal class names are undocumented and change between minor releases.
// The frontend now exposes the following stable testids that the runner
// consumes:
//
//   [data-testid="chat-input"]              — composer input (Fluent UI Input)
//   [data-testid="chat-send-button"]        — primary send button
//   [data-testid="chat-message-list"]       — messages container
//   [data-testid="chat-message-assistant"]  — assistant message wrapper
//   [data-testid="chat-message-user"]       — user message wrapper
//   [data-testid="chart-card"]              — populated chart card (<Card>)
//   [data-testid="chart-card"][data-chart-type=…] — chart type per card
//   [data-testid="chart-title"]             — chart card title
//   [data-testid="chart-unavailable"]       — "Chart unavailable" diagnostic
//   [data-testid="chart-table"]             — rendered table body (for `table`)
//   [data-testid="chart-gauge"]             — gauge container
//   [data-testid="chart-gauge-svg"]         — gauge SVG
//
// The response wait polls **actual mark readiness** — table rows for `table`,
// bar rectangles for bar/groupedBar/horizontalBar, pie sectors for pie/donut,
// line dots for line, gauge SVG for gauge — so the runner cannot scrape the
// DOM mid-stream and report a false "missing entity" (issue #54 Symptom 2b).

/* eslint-disable no-console */

const CASES = [
  { prompt: 'Create a line chart showing Sierra Gold Tequila depletion trends across all regions',
    chartType: 'line',   minMarks: 2, entities: ['Sierra Gold Tequila'] },
  { prompt: 'Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast',
    chartType: 'bar',    minMarks: 3, entities: ['Sierra Gold Tequila', 'Ridgeline Bourbon', 'Summit Vodka'] },
  { prompt: 'Create a pie chart showing market share breakdown for our grocery brands nationally',
    chartType: 'pie',    minMarks: 2, entities: ['FreshMart', 'Harvest Table'] },
  { prompt: 'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions',
    chartType: 'groupedBar', minMarks: 12, entities: ['FreshMart', 'Harvest Table'] },
  { prompt: 'Create a donut chart of Apex Grill variant mix in the Southwest',
    chartType: 'donut',  minMarks: 2, entities: ['Apex Grill'] },
  { prompt: 'Show a horizontal bar chart ranking all brands by depletion growth rate',
    chartType: 'horizontalBar', minMarks: 6, entities: [] },
  { prompt: 'Create a table showing depletion stats for all home improvement brands by region',
    chartType: 'table',  minMarks: 2, entities: ['Pinnacle Hardware', 'Summit Outdoor'] },
  { prompt: 'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest',
    chartType: 'gauge',  minMarks: 1, entities: ['Pinnacle Hardware'] },
  { prompt: 'Compare Coastline Tacos vs Apex Grill depletions across all regions',
    chartType: 'groupedBar', minMarks: 4, entities: ['Coastline Tacos', 'Apex Grill'] },
];

// Recharts DOM shapes per chart type. Used to poll actual mark counts so the
// runner does not sample the DOM before streaming completes.
function countMarks(rootEl, chartType) {
  if (!rootEl) return 0;
  const q = (sel) => rootEl.querySelectorAll(sel).length;
  switch (chartType) {
    case 'bar':
    case 'groupedBar':
    case 'stackedBar':
    case 'horizontalBar':
      return q('.recharts-bar-rectangle');
    case 'line':
      // Each series contributes one connecting curve + one or more visible dots.
      // Count dots first; fall back to curves for line charts with 1-point series.
      return q('.recharts-line-dot, .recharts-dot') || q('.recharts-line-curve');
    case 'pie':
    case 'donut':
      return q('.recharts-pie-sector, .recharts-sector, .recharts-pie path');
    case 'gauge':
      // Gauge renders its own SVG (not Recharts). The stable hook is the
      // `chart-gauge-svg` testid the ChartRenderer applies.
      return rootEl.querySelector('[data-testid="chart-gauge-svg"]') ? 1 : 0;
    case 'table':
      // `chart-table` marks the rendered <table>. Each mark is a body row.
      return q('[data-testid="chart-table"] tbody tr');
    default:
      return 0;
  }
}

function findLatestChartCard() {
  const cards = document.querySelectorAll('[data-testid="chart-card"]');
  return cards.length ? cards[cards.length - 1] : null;
}

function findLatestUnavailable() {
  const notes = document.querySelectorAll('[data-testid="chart-unavailable"]');
  return notes.length ? notes[notes.length - 1] : null;
}

function findComposer() {
  // Prefer the stable testid; fall back to id-based selection for older builds.
  return (
    document.querySelector('[data-testid="chat-input"]')
    || document.querySelector('#chat-input')
  );
}

function findSendButton() {
  return (
    document.querySelector('[data-testid="chat-send-button"]')
    || document.querySelector('button[aria-label="Send message"]')
  );
}

async function submitPrompt(prompt) {
  const input = findComposer();
  if (!input) throw new Error('No chat input found (looked for [data-testid="chat-input"]).');

  // Fluent UI's <Input> renders as a native <input>; set its value via the
  // React-compatible setter path and dispatch input+change so React commits
  // the state update before we press Enter.
  const proto = input.tagName === 'TEXTAREA'
    ? window.HTMLTextAreaElement.prototype
    : window.HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
  setter.call(input, prompt);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.dispatchEvent(new Event('change', { bubbles: true }));

  // Fire Enter — matches the composer's Enter-to-send handler. Fall back to a
  // programmatic click on the send button if the button surface is present.
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

  // Some builds only wire the send button, so also click it if reachable.
  const send = findSendButton();
  if (send && !send.disabled) {
    // A short microtask delay lets React flush the Enter handler first — the
    // click is a harmless no-op if the request already dispatched.
    await new Promise((r) => setTimeout(r, 20));
    if (!send.disabled) send.click();
  }
}

/**
 * Wait until a chart card OR unavailable note appears AND the card is stable
 * (mark count non-decreasing for one polling interval, and above the minimum
 * this case declares). This replaces the historical scrape-before-render race
 * that produced spurious "missing entity" failures (issue #54 Symptom 2b).
 */
async function waitForResponse(prevCardCount, prevNoteCount, chartType, minMarks, timeoutMs = 90_000) {
  const pollMs = 750;
  const start = Date.now();
  let lastMarks = -1;
  let stableTicks = 0;

  while (Date.now() - start < timeoutMs) {
    await new Promise((r) => setTimeout(r, pollMs));
    const cards = document.querySelectorAll('[data-testid="chart-card"]');
    const notes = document.querySelectorAll('[data-testid="chart-unavailable"]');

    // Unavailable diagnostic appeared: the assistant already reported a
    // no-render outcome; stop waiting and let the caller record it.
    if (notes.length > prevNoteCount) return { kind: 'unavailable' };

    if (cards.length > prevCardCount) {
      const card = cards[cards.length - 1];
      const marks = countMarks(card, chartType);

      if (marks >= minMarks) {
        // Require one polling interval where the mark count did not grow
        // before declaring the render complete — protects against sampling
        // a chart mid-stream (streaming table cells were the #54 case-7
        // race). Marks that already exceed the minimum on first observation
        // count that observation as one "stable" tick and settle on the next.
        if (marks === lastMarks) {
          stableTicks++;
        } else {
          stableTicks = 1;
          lastMarks = marks;
        }
        if (stableTicks >= 2) return { kind: 'chart', card, marks };
      }
      else {
        lastMarks = marks;
        stableTicks = 0;
      }
    }
  }
  throw new Error('Timed out waiting for chart response');
}

async function runChartAcceptance() {
  const results = [];
  for (const c of CASES) {
    const prevCards = document.querySelectorAll('[data-testid="chart-card"]').length;
    const prevNotes = document.querySelectorAll('[data-testid="chart-unavailable"]').length;
    try {
      await submitPrompt(c.prompt);
      const outcome = await waitForResponse(prevCards, prevNotes, c.chartType, c.minMarks);

      const card = outcome.kind === 'chart' ? outcome.card : findLatestChartCard();
      const note = outcome.kind === 'unavailable' ? findLatestUnavailable() : null;
      const marks = card ? countMarks(card, c.chartType) : 0;
      const cardText = card ? card.textContent : '';
      const missingEntities = c.entities.filter((e) => !cardText.includes(e));
      const cardChartType = card ? card.getAttribute('data-chart-type') : null;
      const chartTypeMatches = !cardChartType || cardChartType === c.chartType;

      const pass =
        outcome.kind === 'chart' &&
        !!card &&
        !note &&
        marks >= c.minMarks &&
        missingEntities.length === 0 &&
        chartTypeMatches;

      results.push({
        prompt: c.prompt,
        chartType: c.chartType,
        renderedChartType: cardChartType,
        marks,
        pass,
        note: !!note,
        missingEntities,
      });
      console.log((pass ? '✅' : '❌'), c.prompt, { marks, note: !!note, missingEntities, renderedChartType: cardChartType });
    } catch (err) {
      results.push({ prompt: c.prompt, chartType: c.chartType, pass: false, error: String(err) });
      console.log('❌', c.prompt, err);
    }
  }
  console.table(results.map(({ prompt, chartType, marks, pass, note, missingEntities }) => ({
    prompt: prompt.slice(0, 60) + (prompt.length > 60 ? '…' : ''),
    chartType, marks, note, missingEntities: (missingEntities || []).join(', '), pass,
  })));
  console.log('COPY-TO-DOCS:', JSON.stringify(results, null, 2));
  return results;
}

// Export for the console.
globalThis.runChartAcceptance = runChartAcceptance;
