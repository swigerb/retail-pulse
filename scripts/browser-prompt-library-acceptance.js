// Curated PROSE prompt-library browser acceptance runner (issue #63).
//
// Paste this into the browser DevTools console while the Retail Pulse frontend
// is loaded. It walks every curated PROSE prompt from
// `ProsePromptAcceptanceManifest` — every non-chart entry from every
// `PROMPT_CATEGORIES` category in `src/RetailPulse.Web/src/constants/prompts.ts`
// — submits the prompt through the composer, waits for the assistant's
// response to fully render, and asserts:
//
//   • the assistant produced a non-empty prose reply (not just an empty
//     card, not just the "Chart unavailable" diagnostic),
//   • the response did not accidentally render a chart card (a prose
//     prompt that renders a chart is the classic "chart JSON leaked into
//     a prose answer" regression),
//   • the response text does not carry a raw ```json …``` fence or a
//     `"chart":` / `"chartSpec":` payload (defence-in-depth against chart
//     JSON leaking as pre-rendered text), and
//   • the routing indicator (when present) points at the specialist the
//     manifest expects — never the Consensus Council.
//
// Sibling of `scripts/browser-chart-acceptance.js` (which covers the Charts
// category); together the two runners exercise the entire curated prompt
// library.
//
// Usage:
//   1. cd src/RetailPulse.Web && npm run dev
//   2. Start the API (RetailPulse.AppHost or dotnet run per-project).
//   3. Open http://localhost:5173, sign in, land on the empty dashboard.
//   4. Open DevTools → Console.
//   5. Paste this file's contents and run `await runPromptLibraryAcceptance()`.
//   6. Copy the resulting `results` array into the issue #63 comment on #59.
//
// This script is deliberately dependency-free JS (not TS) and shares the same
// stable `data-testid` selectors as the chart runner.

/* eslint-disable no-console */

// Mirror of ProsePromptAcceptanceManifest.Cases (issue #63). Any drift is
// caught by the backend `ProsePromptAcceptanceManifestContractTests` /
// `ProsePromptRoutingAcceptanceTests` suites in CI; the fields here are the
// ones a live browser sweep needs.
const PROSE_CASES = [
  { prompt: 'Compare depletion trends across all regions for this quarter',
    categoryId: 'general', expectedIntent: 'demand/forecasting' },
  { prompt: 'Which brands are growing fastest year-over-year across the portfolio?',
    categoryId: 'general', expectedIntent: 'general/fallback' },
  { prompt: 'Show me field sentiment for our top 3 brands in the Southeast',
    categoryId: 'general', expectedIntent: 'sentiment/field' },

  { prompt: 'How are FreshMart depletions trending in the Northeast this quarter?',
    categoryId: 'grocery', expectedIntent: 'general/fallback' },
  { prompt: 'Compare Harvest Table vs FreshMart sell-through rates by region',
    categoryId: 'grocery', expectedIntent: 'demand/forecasting' },
  { prompt: 'What is the field sentiment for Harvest Table Meal Kits in the Midwest?',
    categoryId: 'grocery', expectedIntent: 'sentiment/field' },

  { prompt: 'How is Apex Grill performing in the Southwest this quarter?',
    categoryId: 'qsr', expectedIntent: 'general/fallback' },
  { prompt: 'What is the field sentiment for Coastline Tacos in the West Coast?',
    categoryId: 'qsr', expectedIntent: 'sentiment/field' },

  { prompt: 'Show me Pinnacle Hardware depletion stats in the Midwest for Q1',
    categoryId: 'home-improvement', expectedIntent: 'general/fallback' },
  { prompt: 'How is Summit Outdoor performing in the Southeast vs West Coast?',
    categoryId: 'home-improvement', expectedIntent: 'general/fallback' },
  { prompt: 'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?',
    categoryId: 'home-improvement', expectedIntent: 'sentiment/field' },

  { prompt: 'How are ClearDesk depletions trending in the Northeast this quarter?',
    categoryId: 'office-supply', expectedIntent: 'general/fallback' },
  { prompt: 'Compare ClearDesk Technology vs Paper Products sell-through by region',
    categoryId: 'office-supply', expectedIntent: 'demand/forecasting' },
  { prompt: 'What is the field sentiment for ClearDesk in the Southeast?',
    categoryId: 'office-supply', expectedIntent: 'sentiment/field' },

  { prompt: 'Show me Urban Living depletion trends across all regions this quarter',
    categoryId: 'furniture', expectedIntent: 'general/fallback' },
  { prompt: 'Compare Foundry Home vs Urban Living performance in the West Coast',
    categoryId: 'furniture', expectedIntent: 'demand/forecasting' },
  { prompt: 'What is the field sentiment for Urban Living in the Pacific Northwest?',
    categoryId: 'furniture', expectedIntent: 'sentiment/field' },
];

const COUNCIL_INTENT = 'council/health';

// Rough regex for chart JSON that has leaked into a prose bubble. Prose prompts
// must never surface any of these fragments (`ChartRenderer` renders a chart
// card instead of writing raw JSON into a message).
const CHART_JSON_LEAK = /```json[\s\S]*?"(?:chart|chartSpec|charts)"\s*:|"(?:chartSpec|chart)"\s*:\s*\{/i;

function findComposer() {
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

function findLatestAssistantMessage() {
  const nodes = document.querySelectorAll('[data-testid="chat-message-assistant"]');
  return nodes.length ? nodes[nodes.length - 1] : null;
}

function findRoutingLabel(assistantMsgEl) {
  if (!assistantMsgEl) return null;
  // The AgentRoutingIndicator exposes `data-testid="execution-path-pill"` and
  // labels the routed intent inside the same subtree.
  const pill = assistantMsgEl.querySelector('[data-testid="execution-path-pill"]');
  return pill ? pill.textContent.trim() : null;
}

async function submitPrompt(prompt) {
  const input = findComposer();
  if (!input) throw new Error('No chat input found (looked for [data-testid="chat-input"]).');

  const proto = input.tagName === 'TEXTAREA'
    ? window.HTMLTextAreaElement.prototype
    : window.HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
  setter.call(input, prompt);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.dispatchEvent(new Event('change', { bubbles: true }));
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

  const send = findSendButton();
  if (send && !send.disabled) {
    await new Promise((r) => setTimeout(r, 20));
    if (!send.disabled) send.click();
  }
}

/**
 * Wait until a new assistant message appears AND its streaming flag has
 * settled (data-message-streaming="false"). Also detects chart-card /
 * chart-unavailable elements attached to the same message so a leaked
 * chart is captured.
 */
async function waitForAssistantResponse(prevAssistantCount, timeoutMs = 90_000) {
  const pollMs = 750;
  const start = Date.now();
  let stableTicks = 0;
  let lastLength = -1;

  while (Date.now() - start < timeoutMs) {
    await new Promise((r) => setTimeout(r, pollMs));
    const assistantMsgs = document.querySelectorAll('[data-testid="chat-message-assistant"]');
    if (assistantMsgs.length <= prevAssistantCount) continue;

    const latest = assistantMsgs[assistantMsgs.length - 1];
    const streaming = latest.getAttribute('data-message-streaming') === 'true';
    const text = latest.textContent || '';

    if (!streaming && text.length > 0) {
      // Require one polling interval where the text did not grow before
      // declaring the response complete — protects against sampling
      // mid-stream (issue #54 case-7 race analogue for prose).
      if (text.length === lastLength) {
        stableTicks++;
      } else {
        stableTicks = 1;
        lastLength = text.length;
      }
      if (stableTicks >= 2) return latest;
    } else {
      lastLength = text.length;
      stableTicks = 0;
    }
  }
  throw new Error('Timed out waiting for assistant response');
}

async function runPromptLibraryAcceptance() {
  const results = [];
  for (const c of PROSE_CASES) {
    const prevAssistant = document.querySelectorAll('[data-testid="chat-message-assistant"]').length;
    try {
      await submitPrompt(c.prompt);
      const assistantMsg = await waitForAssistantResponse(prevAssistant);
      const text = (assistantMsg && assistantMsg.textContent) || '';
      const trimmed = text.trim();

      const chartCard = assistantMsg && assistantMsg.querySelector('[data-testid="chart-card"]');
      const chartUnavailable = assistantMsg && assistantMsg.querySelector('[data-testid="chart-unavailable"]');
      const routing = findRoutingLabel(assistantMsg);

      const leaked = CHART_JSON_LEAK.test(text);
      const nonEmpty = trimmed.length >= 20;
      const notCouncil = !routing || !routing.toLowerCase().includes('council');

      const pass =
        !!assistantMsg
        && !chartCard
        && !chartUnavailable
        && !leaked
        && nonEmpty
        && notCouncil;

      results.push({
        prompt: c.prompt,
        categoryId: c.categoryId,
        expectedIntent: c.expectedIntent,
        observedRouting: routing,
        responseLength: trimmed.length,
        pass,
        chartCardRendered: !!chartCard,
        chartUnavailableRendered: !!chartUnavailable,
        chartJsonLeaked: leaked,
        wentToCouncil: !notCouncil,
      });
      console.log((pass ? '✅' : '❌'), c.prompt, {
        length: trimmed.length,
        routing,
        chart: !!chartCard,
        unavailable: !!chartUnavailable,
        leaked,
      });
    } catch (err) {
      results.push({
        prompt: c.prompt,
        categoryId: c.categoryId,
        expectedIntent: c.expectedIntent,
        pass: false,
        error: String(err),
      });
      console.log('❌', c.prompt, err);
    }
  }

  console.table(results.map(({ prompt, categoryId, expectedIntent, observedRouting, responseLength, pass }) => ({
    prompt: prompt.slice(0, 55) + (prompt.length > 55 ? '…' : ''),
    categoryId,
    expectedIntent,
    observedRouting,
    responseLength,
    pass,
  })));
  console.log('COPY-TO-DOCS:', JSON.stringify(results, null, 2));
  return results;
}

globalThis.runPromptLibraryAcceptance = runPromptLibraryAcceptance;
