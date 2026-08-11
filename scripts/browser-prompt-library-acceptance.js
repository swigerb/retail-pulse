// Curated NON-chart prompt browser acceptance runner (issue #61).
//
// Companion to `browser-chart-acceptance.js`. Where the chart runner exercises the
// 9 explicit chart acceptance cases, this runner walks the 17 curated prompts across
// the six domain categories (General Retail, Grocery, QSR, Home Improvement, Office
// Supply, Furniture) that are expected to return a PROSE narrative — not a chart —
// and asserts the invariants the production sweep depends on:
//
//   • the assistant returned a non-empty prose response,
//   • the response did NOT render a `[class*="chartCard"]` — a chart appearing for
//     a prose prompt means the router misclassified it (issue #50 regression class),
//   • the response did NOT contain a `[role="note"]` "Chart unavailable" diagnostic,
//   • the response did NOT leak raw chart JSON (`{"type":"...","title":"...","data":`),
//     which would mean the sanitizer regressed,
//   • the response did NOT surface an assistant-facing fallback error string
//     ("I couldn't complete that request", "internal error", "unhandled error").
//
// Paste this file's contents into DevTools Console while signed in at
// https://calm-wave-04edb640f.7.azurestaticapps.net/ and run
// `await runPromptLibraryAcceptance()`. Copy the `COPY-TO-DOCS:` payload into
// `docs/testing/production-prompt-acceptance-results.md`.
//
// Dependency-free JS (not TS) so it can be pasted verbatim into any recent browser
// console with no build step. Mirrors the polling / stable-selector approach used
// by the chart runner so both surfaces share a single browser-side pattern.

/* eslint-disable no-console */

const CASES = [
  // General Retail
  { category: 'general', prompt: 'Compare depletion trends across all regions for this quarter' },
  { category: 'general', prompt: 'Which brands are growing fastest year-over-year across the portfolio?' },
  { category: 'general', prompt: 'Show me field sentiment for our top 3 brands in the Southeast' },
  // Grocery
  { category: 'grocery', prompt: 'How are FreshMart depletions trending in the Northeast this quarter?' },
  { category: 'grocery', prompt: 'Compare Harvest Table vs FreshMart sell-through rates by region' },
  { category: 'grocery', prompt: 'What is the field sentiment for Harvest Table Meal Kits in the Midwest?' },
  // QSR (excluding the Coastline-vs-Apex comparison which is a CHART acceptance case)
  { category: 'qsr', prompt: 'How is Apex Grill performing in the Southwest this quarter?' },
  { category: 'qsr', prompt: 'What is the field sentiment for Coastline Tacos in the West Coast?' },
  // Home Improvement
  { category: 'home-improvement', prompt: 'Show me Pinnacle Hardware depletion stats in the Midwest for Q1' },
  { category: 'home-improvement', prompt: 'How is Summit Outdoor performing in the Southeast vs West Coast?' },
  { category: 'home-improvement', prompt: 'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?' },
  // Office Supply
  { category: 'office-supply', prompt: 'How are ClearDesk depletions trending in the Northeast this quarter?' },
  { category: 'office-supply', prompt: 'Compare ClearDesk Technology vs Paper Products sell-through by region' },
  { category: 'office-supply', prompt: 'What is the field sentiment for ClearDesk in the Southeast?' },
  // Furniture
  { category: 'furniture', prompt: 'Show me Urban Living depletion trends across all regions this quarter' },
  { category: 'furniture', prompt: 'Compare Foundry Home vs Urban Living performance in the West Coast' },
  { category: 'furniture', prompt: 'What is the field sentiment for Urban Living in the Pacific Northwest?' },
];

// Signatures that would indicate the response failed even though something rendered.
const JSON_LEAKAGE_RE = /\{\s*"(?:type|chartType|title|series|data)"\s*:/;
const ASSISTANT_FALLBACK_RES = [
  /i (?:couldn'?t|was unable to) complete/i,
  /internal (?:server )?error/i,
  /unhandled error/i,
  /something went wrong/i,
];

function assistantBubbles() {
  // Every rendered assistant turn lives inside an element whose className contains
  // "message" and whose data attribute (or aria-label) marks it as assistant. We
  // fall back to selecting all message-like containers and taking the last one.
  const candidates = document.querySelectorAll(
    '[class*="assistantMessage"], [class*="messageAssistant"], [class*="chatMessage"], [class*="message"]');
  return Array.from(candidates);
}

function findLatestAssistantBubble(prevCount) {
  const all = assistantBubbles();
  return all.length > prevCount ? all[all.length - 1] : null;
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
  const textarea = document.querySelector('textarea');
  if (!textarea) throw new Error('No textarea found in the DOM.');
  const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
  setter.call(textarea, prompt);
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
  textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
}

async function waitForResponse(prevBubbleCount, prevChartCount, prevNoteCount, timeoutMs = 90_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    await new Promise((r) => setTimeout(r, 750));
    const bubbles = assistantBubbles().length;
    const charts = document.querySelectorAll('[class*="chartCard"]').length;
    const notes = document.querySelectorAll('[role="note"]').length;
    if (bubbles > prevBubbleCount || charts > prevChartCount || notes > prevNoteCount) {
      // Give the streaming tail a beat to finalize before we inspect it.
      await new Promise((r) => setTimeout(r, 1500));
      return;
    }
  }
  throw new Error('Timed out waiting for assistant response');
}

async function runPromptLibraryAcceptance() {
  const results = [];
  for (const c of CASES) {
    const prevBubbles = assistantBubbles().length;
    const prevCharts = document.querySelectorAll('[class*="chartCard"]').length;
    const prevNotes = document.querySelectorAll('[role="note"]').length;
    try {
      await submitPrompt(c.prompt);
      await waitForResponse(prevBubbles, prevCharts, prevNotes);

      const bubble = findLatestAssistantBubble(prevBubbles);
      const bubbleText = bubble ? bubble.textContent.trim() : '';
      const newChart = document.querySelectorAll('[class*="chartCard"]').length > prevCharts;
      const newNote = document.querySelectorAll('[role="note"]').length > prevNotes;

      const jsonLeaked = JSON_LEAKAGE_RE.test(bubbleText);
      const fallbackError = ASSISTANT_FALLBACK_RES.find((r) => r.test(bubbleText));

      const failures = [];
      if (!bubble || bubbleText.length < 40) failures.push('empty-or-tiny-prose');
      if (newChart) failures.push('unexpected-chart');
      if (newNote) failures.push('chart-unavailable-note');
      if (jsonLeaked) failures.push('chart-json-leakage');
      if (fallbackError) failures.push('assistant-fallback-error');

      const pass = failures.length === 0;
      results.push({
        category: c.category,
        prompt: c.prompt,
        pass,
        proseLen: bubbleText.length,
        newChart,
        newNote,
        jsonLeaked,
        failures,
      });
      console.log((pass ? '✅' : '❌'), c.prompt, { proseLen: bubbleText.length, failures });
    } catch (err) {
      results.push({ category: c.category, prompt: c.prompt, pass: false, error: String(err) });
      console.log('❌', c.prompt, err);
    }
  }
  console.table(results.map(({ category, prompt, pass, proseLen, failures }) => ({
    category,
    prompt: prompt.slice(0, 60) + (prompt.length > 60 ? '…' : ''),
    proseLen,
    failures: (failures || []).join(', '),
    pass,
  })));
  console.log('COPY-TO-DOCS:', JSON.stringify(results, null, 2));
  return results;
}

globalThis.runPromptLibraryAcceptance = runPromptLibraryAcceptance;
