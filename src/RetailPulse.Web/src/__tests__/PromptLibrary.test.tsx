import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { PromptLibrary } from '../components/PromptLibrary';
import { PROMPT_CATEGORIES, type PromptCategory } from '../constants/prompts';

function renderLibrary(props: Partial<React.ComponentProps<typeof PromptLibrary>> = {}) {
  const onSelect = props.onSelect ?? vi.fn();
  render(
    <FluentProvider theme={teamsDarkTheme}>
      <PromptLibrary categories={PROMPT_CATEGORIES} onSelect={onSelect} {...props} />
    </FluentProvider>,
  );
  return { onSelect };
}

// The panel renders in a Fluent trap-focus Popover portal. Under jsdom (especially
// in CI) tabster can nondeterministically apply aria-hidden to the active surface,
// which hides it from default role queries. Text queries ignore aria-hidden, so we
// anchor "is the panel open" on the heading text, then assert the real ARIA contract
// (role=dialog + accessible name + aria-modal) on the actual surface element.
async function openedPanel() {
  const heading = await screen.findByText('Prompt library', { selector: 'h2' }, { timeout: 4000 });
  const surface = heading.closest('[role="dialog"]') as HTMLElement;
  expect(surface).not.toBeNull();
  expect(surface).toHaveAttribute('aria-label', 'Prompt library');
  expect(surface).toHaveAttribute('aria-modal', 'true');
  return { surface, panel: within(surface) };
}

const GENERAL_PROMPT = 'Compare depletion trends across all regions for this quarter';
const GROCERY_PROMPT = 'How are FreshMart depletions trending in the Northeast this quarter?';

describe('PromptLibrary', () => {
  it('renders a labeled trigger and keeps the panel closed until opened', () => {
    renderLibrary();

    const trigger = screen.getByRole('button', { name: /Prompt ideas/i });
    expect(trigger).toBeInTheDocument();
    // Panel content is not rendered while closed.
    expect(screen.queryByText(/Prompt library/i)).not.toBeInTheDocument();
  });

  it('opens the categorized panel on click with an accessible name and category group', async () => {
    const user = userEvent.setup();
    renderLibrary();

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));

    const { panel } = await openedPanel();

    // Category filters live in a labeled group; "All" is selected by default.
    expect(panel.getByRole('group', { name: /Prompt categories/i, hidden: true })).toBeInTheDocument();
    expect(panel.getByRole('button', { name: /🏪 All/i, hidden: true })).toHaveAttribute('aria-pressed', 'true');
    expect(panel.getByRole('button', { name: /🛒 Grocery/i, hidden: true })).toHaveAttribute('aria-pressed', 'false');
  });

  it('filters prompts when a category is selected', async () => {
    const user = userEvent.setup();
    renderLibrary();

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));

    const { panel } = await openedPanel();

    // "All" view surfaces prompts from multiple categories.
    expect(panel.getByRole('button', { name: GENERAL_PROMPT, hidden: true })).toBeInTheDocument();
    expect(panel.getByRole('button', { name: GROCERY_PROMPT, hidden: true })).toBeInTheDocument();

    // Selecting Grocery narrows the list to that category only.
    await user.click(panel.getByRole('button', { name: /🛒 Grocery/i, hidden: true }));
    expect(panel.getByRole('button', { name: /🛒 Grocery/i, hidden: true })).toHaveAttribute('aria-pressed', 'true');
    expect(panel.getByRole('button', { name: GROCERY_PROMPT, hidden: true })).toBeInTheDocument();
    await waitFor(() => {
      expect(panel.queryByRole('button', { name: GENERAL_PROMPT, hidden: true })).not.toBeInTheDocument();
    });
  });

  it('sends the chosen prompt and closes the panel', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    renderLibrary({ onSelect });

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    const { panel } = await openedPanel();
    await user.click(panel.getByRole('button', { name: GENERAL_PROMPT, hidden: true }));

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(GENERAL_PROMPT);

    // Panel dismisses after a selection.
    await waitFor(() => {
      expect(screen.queryByText('Prompt library', { selector: 'h2' })).not.toBeInTheDocument();
    });
  });

  it('is keyboard operable: opens with Enter and dismisses with Escape, restoring focus', async () => {
    const user = userEvent.setup();
    renderLibrary();

    const trigger = screen.getByRole('button', { name: /Prompt ideas/i });
    trigger.focus();
    expect(trigger).toHaveFocus();

    await user.keyboard('{Enter}');
    await openedPanel();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByText('Prompt library', { selector: 'h2' })).not.toBeInTheDocument();
    });
    // Focus returns to the trigger for a smooth keyboard flow.
    expect(trigger).toHaveFocus();
  });

  it('disables the trigger when disabled', () => {
    renderLibrary({ disabled: true });
    expect(screen.getByRole('button', { name: /Prompt ideas/i })).toBeDisabled();
  });
});

describe('PromptLibrary — issue #109 task shape', () => {
  function packCategories(): PromptCategory[] {
    return [
      {
        id: 'pack-cat',
        label: 'Pack Category',
        emoji: '🎁',
        order: 1,
        tasks: [
          {
            name: 'Short label A',
            prompt: 'Fully formed question about brand X in region Y',
            capability: { kind: 'chart', chartType: 'line' },
          },
          {
            name: 'Short label B',
            prompt: 'Another fully formed question submitted to the backend',
            capability: { kind: 'plan', planPath: 'planogram-adjacency' },
          },
        ],
        prompts: [
          'Fully formed question about brand X in region Y',
          'Another fully formed question submitted to the backend',
        ],
      },
    ];
  }

  it('renders the display name on the button and submits the full prompt string', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={packCategories()} onSelect={onSelect} />
      </FluentProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    const heading = await screen.findByText('Prompt library', { selector: 'h2' });
    const surface = heading.closest('[role="dialog"]') as HTMLElement;

    const shortLabel = await within(surface).findByRole('button', { name: 'Short label A', hidden: true });
    expect(shortLabel.textContent?.trim()).toBe('Short label A');

    await user.click(shortLabel);

    expect(onSelect).toHaveBeenCalledWith('Fully formed question about brand X in region Y');
  });

  it('exposes capability metadata as data-capability-* attributes so downstream tooling can key on it', async () => {
    const user = userEvent.setup();
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={packCategories()} onSelect={() => {}} />
      </FluentProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    await screen.findByText('Prompt library', { selector: 'h2' });

    const items = await waitFor(() => {
      const els = document.querySelectorAll('[data-testid="prompt-library-item"]');
      if (els.length !== 2) throw new Error(`expected 2 items, saw ${els.length}`);
      return Array.from(els) as HTMLButtonElement[];
    });

    expect(items[0].getAttribute('data-capability-kind')).toBe('chart');
    expect(items[0].getAttribute('data-capability-chart-type')).toBe('line');
    expect(items[1].getAttribute('data-capability-kind')).toBe('plan');
    expect(items[1].getAttribute('data-capability-plan-path')).toBe('planogram-adjacency');
  });

  it('respects explicit task-level ordering when rendering within a category', async () => {
    const user = userEvent.setup();
    const categories: PromptCategory[] = [
      {
        id: 'ord',
        label: 'Ordering',
        emoji: '🔀',
        tasks: [
          { name: 'Delta', prompt: 'p-delta', order: 3 },
          { name: 'Alpha', prompt: 'p-alpha', order: 1 },
          { name: 'Bravo', prompt: 'p-bravo', order: 2 },
        ],
        // The prompts array intentionally mirrors task.prompt order for
        // legacy consumers; the DOM order comes from tasks.
        prompts: ['p-delta', 'p-alpha', 'p-bravo'],
      },
    ];
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={categories} onSelect={() => {}} />
      </FluentProvider>,
    );
    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    await screen.findByText('Prompt library', { selector: 'h2' });

    // The component renders tasks in the array order it received them, so
    // the ordering contract lives at the pack normalization boundary and is
    // reflected verbatim in the DOM. This test proves the DOM does not
    // silently re-sort.
    const items = await waitFor(() => {
      const els = document.querySelectorAll('[data-testid="prompt-library-item"]');
      if (els.length !== 3) throw new Error(`expected 3 items, saw ${els.length}`);
      return Array.from(els) as HTMLButtonElement[];
    });
    expect(items.map((el) => el.textContent?.trim())).toEqual(['Delta', 'Alpha', 'Bravo']);
  });

  it('renders an accessible empty state when no categories are supplied', async () => {
    const user = userEvent.setup();
    render(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={[]} onSelect={() => {}} />
      </FluentProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    await screen.findByText('Prompt library', { selector: 'h2' });

    const empty = await waitFor(() => {
      const el = document.querySelector('[data-testid="prompt-library-empty"]');
      if (!el) throw new Error('empty state not mounted');
      return el as HTMLElement;
    });

    expect(empty).toHaveAttribute('role', 'status');
    expect(empty.textContent).toMatch(/no starting tasks/i);

    // The category chip row is hidden when there are no categories so a
    // stray "All" chip does not linger next to the empty state.
    expect(document.querySelector('[data-testid="prompt-library-categories"]')).toBeNull();
  });

  it('supports pack-switching by re-rendering with a fresh categories prop', async () => {
    const user = userEvent.setup();
    const initial = packCategories();
    const { rerender } = render(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={initial} onSelect={() => {}} />
      </FluentProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    await screen.findByText('Prompt library', { selector: 'h2' });
    expect(document.querySelector('[data-testid="prompt-library-category-pack-cat"]')).not.toBeNull();

    // Simulate the Dashboard swapping in a second pack's categories after
    // /api/pack/starting-tasks resolves for a different Packs:Active setting.
    const swapped: PromptCategory[] = [
      {
        id: 'swap-cat',
        label: 'Swapped Category',
        emoji: '🔁',
        tasks: [{ name: 'Swap task', prompt: 'swap-prompt' }],
        prompts: ['swap-prompt'],
      },
    ];
    rerender(
      <FluentProvider theme={teamsDarkTheme}>
        <PromptLibrary categories={swapped} onSelect={() => {}} />
      </FluentProvider>,
    );

    await waitFor(() => {
      expect(document.querySelector('[data-testid="prompt-library-category-swap-cat"]')).not.toBeNull();
      expect(document.querySelector('[data-testid="prompt-library-category-pack-cat"]')).toBeNull();
    });
  });
});
