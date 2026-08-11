import { describe, it, expect, vi } from 'vitest';
import React from 'react';
import { render, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { PromptLibrary } from '../components/PromptLibrary';
import { PROMPT_CATEGORIES } from '../constants/prompts';
import { PROMPT_ACCEPTANCE_CASES } from '../components/promptAcceptance';

/**
 * Production-capable "browser" style acceptance for the Prompt Ideas popover.
 *
 * "Browser-style" here means:
 *   • Selectors are stable `data-testid` anchors and role/name pairs — never
 *     Griffel class name substrings (which change on every Fluent release and
 *     across build modes).
 *   • Assertions poll with `waitFor` / `findBy*` instead of racing on
 *     synchronous DOM, so the test survives Fluent's portal-mount timing and
 *     tabster's aria-hidden dance under jsdom, and would translate cleanly to
 *     a real browser runner (Playwright / Cypress) if wired up.
 *   • Every one of the 27 curated Prompt Ideas is proven discoverable through
 *     the popover's own UX — trigger click, category filter click, prompt
 *     click — with no back-door selector into private component state.
 *
 * This test is intentionally exhaustive: an editor change that hides a prompt
 * behind a broken category filter, or a Fluent version that reworks the popover
 * portal, will trip a specific failing case pointing at the offending prompt.
 */

function renderLibrary(props: Partial<React.ComponentProps<typeof PromptLibrary>> = {}) {
  const onSelect = props.onSelect ?? vi.fn();
  const utils = render(
    <FluentProvider theme={webDarkTheme}>
      <PromptLibrary categories={PROMPT_CATEGORIES} onSelect={onSelect} {...props} />
    </FluentProvider>,
  );
  return { onSelect, ...utils };
}

/** Poll until the popover panel is mounted, then return its DOM node. */
async function openPanel(user: ReturnType<typeof userEvent.setup>): Promise<HTMLElement> {
  const trigger = document.querySelector('[data-testid="prompt-library-trigger"]') as HTMLElement | null;
  expect(trigger, 'prompt-library-trigger testid must exist').not.toBeNull();
  await user.click(trigger!);
  const panel = await waitFor(
    () => {
      const el = document.querySelector('[data-testid="prompt-library-panel"]');
      if (!el) throw new Error('panel not mounted yet');
      return el as HTMLElement;
    },
    { timeout: 5000 },
  );
  return panel;
}

describe('Prompt Ideas — production-style browser acceptance', () => {
  it('exposes exactly the 27 acceptance-manifest prompts through the popover (all-categories view)', async () => {
    const user = userEvent.setup();
    renderLibrary();
    const panel = await openPanel(user);

    // Wait for the "All" category items list to fully mount.
    await waitFor(() => {
      const items = panel.querySelectorAll('[data-testid="prompt-library-item"]');
      // "All" view mounts every prompt.
      expect(items.length).toBe(PROMPT_ACCEPTANCE_CASES.length);
    });

    const items = Array.from(
      panel.querySelectorAll<HTMLButtonElement>('[data-testid="prompt-library-item"]'),
    );
    const textInOrder = items.map((el) => el.textContent?.trim() ?? '');
    const expected = PROMPT_ACCEPTANCE_CASES.map((c) => c.prompt);
    expect(textInOrder).toEqual(expected);
  });

  it.each(PROMPT_CATEGORIES.map((c) => [c.id, c.label] as const))(
    'filters to only the "%s" category prompts when its chip is clicked',
    async (categoryId) => {
      const user = userEvent.setup();
      renderLibrary();
      const panel = await openPanel(user);

      const chip = panel.querySelector(
        `[data-testid="prompt-library-category-${categoryId}"]`,
      ) as HTMLButtonElement | null;
      expect(chip, `category chip for '${categoryId}' must exist`).not.toBeNull();
      await user.click(chip!);

      const categoryPrompts = PROMPT_ACCEPTANCE_CASES.filter((c) => c.categoryId === categoryId).map(
        (c) => c.prompt,
      );

      await waitFor(() => {
        const items = panel.querySelectorAll<HTMLButtonElement>(
          '[data-testid="prompt-library-item"]',
        );
        expect(items.length).toBe(categoryPrompts.length);
        expect(Array.from(items).map((el) => el.textContent?.trim())).toEqual(categoryPrompts);
      });

      // aria-pressed state on the active chip is the ARIA contract; testid+state
      // both must move together.
      expect(chip!.getAttribute('aria-pressed')).toBe('true');
    },
  );

  it.each(PROMPT_ACCEPTANCE_CASES.map((c) => [c.id, c.prompt] as const))(
    'dispatches "%s" (%s) through the popover selection flow',
    async (id, promptText) => {
      const user = userEvent.setup();
      const onSelect = vi.fn();
      renderLibrary({ onSelect });
      const panel = await openPanel(user);

      // Find the button whose textContent exactly matches the featured prompt.
      const button = await waitFor(() => {
        const items = Array.from(
          panel.querySelectorAll<HTMLButtonElement>('[data-testid="prompt-library-item"]'),
        );
        const hit = items.find((el) => el.textContent?.trim() === promptText);
        if (!hit) throw new Error(`prompt '${promptText}' (${id}) not yet mounted`);
        return hit;
      });

      await user.click(button);

      await waitFor(() => {
        expect(onSelect).toHaveBeenCalledWith(promptText);
      });
    },
  );

  it('never leaks a raw chart-spec JSON block into the popover DOM', async () => {
    const user = userEvent.setup();
    renderLibrary();
    const panel = await openPanel(user);
    // Every rendered item text must be plain prompt prose — no JSON payloads
    // from a curated string masquerading as a prompt. Defense against a future
    // author pasting a JSON-shaped prompt into `constants/prompts.ts`.
    const items = panel.querySelectorAll('[data-testid="prompt-library-item"]');
    for (const el of Array.from(items)) {
      const text = el.textContent ?? '';
      expect(text).not.toMatch(/"type"\s*:\s*"(?:line|bar|pie|donut|gauge|table)"/i);
      expect(text).not.toMatch(/"chart_spec"\s*:/);
    }
  });
});
