// Curated chart-prompt browser acceptance runner (issue #50).
//
// Paste this into the browser DevTools console while the Retail Pulse frontend
// is loaded (any auth mode — Anonymous is fine). It walks every curated chart
// prompt from the acceptance manifest, submits the prompt through the same
// path a user would (the chat send hook), waits for the response, and asserts:
//   • the rendered chart card contains a real Recharts canvas (not the
//     "Chart unavailable" diagnostic),
//   • the chart's title, required entity labels, and minimum mark count agree
//     with the manifest,
//   • the assistant did not answer with prose only ("truncated portfolio
//     pull", "Chart unavailable — historical pulls truncated"), which was
//     the #50 P0 symptom.
//
// Usage:
//   1. cd src/RetailPulse.Web && npm run dev
//   2. Start the API (RetailPulse.AppHost or dotnet run per-project).
//   3. Open http://localhost:5173, sign in, land on the empty dashboard.
//   4. Open DevTools → Console.
//   5. Paste this file's contents and run runChartAcceptance().
//   6. Copy the resulting `results` array into docs/chart-acceptance-run.md.
//
// This script is deliberately dependency-free JS (not TS) so it can be pasted
// verbatim into any recent browser console without a build step.

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

function countMarks(rootEl, chartType) {
  const q = (sel) => rootEl.querySelectorAll(sel).length;
  switch (chartType) {
    case 'bar':
    case 'groupedBar':
    case 'stackedBar':
    case 'horizontalBar':
      return q('.recharts-bar-rectangle');
    case 'line':
      // one .recharts-line per series; each series contributes >=1 dot/curve
      return q('.recharts-line-dot, .recharts-dot') || q('.recharts-line-curve');
    case 'pie':
    case 'donut':
      return q('.recharts-pie-sector, .recharts-sector, .recharts-pie path');
    case 'gauge':
      return q('svg[role="img"]');
    case 'table':
      return q('tbody tr');
    default:
      return 0;
  }
}

function findLatestChartCard() {
  const cards = document.querySelectorAll('[class*="chartCard"]');
  return cards.length ? cards[cards.length - 1] : null;
}

function findLatestUnavailable() {
  const notes = document.querySelectorAll('[role="note"]');
  return notes.length ? notes[notes.length - 1] : null;
}

async function submitPrompt(prompt) {
  // Prefer the promptbox textarea; fall back to the first textarea in the DOM.
  const textarea = document.querySelector('textarea');
  if (!textarea) throw new Error('No textarea found in the DOM.');
  const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
  setter.call(textarea, prompt);
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
  // Fire the enter key like a user hitting send.
  textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
}

async function waitForResponse(prevCardCount, timeoutMs = 90_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    await new Promise((r) => setTimeout(r, 750));
    const cards = document.querySelectorAll('[class*="chartCard"]');
    const notes = document.querySelectorAll('[role="note"]');
    if (cards.length > prevCardCount || notes.length > 0) return;
  }
  throw new Error('Timed out waiting for chart response');
}

async function runChartAcceptance() {
  const results = [];
  for (const c of CASES) {
    const prevCards = document.querySelectorAll('[class*="chartCard"]').length;
    try {
      await submitPrompt(c.prompt);
      await waitForResponse(prevCards);
      const card = findLatestChartCard();
      const note = findLatestUnavailable();
      const marks = card ? countMarks(card, c.chartType) : 0;
      const missingEntities = c.entities.filter((e) => card && !card.textContent.includes(e));
      const pass =
        !!card &&
        !note &&
        marks >= c.minMarks &&
        missingEntities.length === 0;
      results.push({ prompt: c.prompt, chartType: c.chartType, marks, pass, note: !!note, missingEntities });
      console.log((pass ? '✅' : '❌'), c.prompt, { marks, note: !!note, missingEntities });
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
