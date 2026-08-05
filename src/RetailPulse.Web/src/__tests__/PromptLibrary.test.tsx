import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import { PromptLibrary } from '../components/PromptLibrary';
import { PROMPT_CATEGORIES } from '../constants/prompts';

function renderLibrary(props: Partial<React.ComponentProps<typeof PromptLibrary>> = {}) {
  const onSelect = props.onSelect ?? vi.fn();
  render(
    <FluentProvider theme={teamsDarkTheme}>
      <PromptLibrary categories={PROMPT_CATEGORIES} onSelect={onSelect} {...props} />
    </FluentProvider>,
  );
  return { onSelect };
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

    // Dialog surface exposes an accessible name and is a modal dialog.
    // Note: Fluent's trap-focus modal can nondeterministically apply aria-hidden
    // to the active surface under jsdom, so query with hidden:true and assert the
    // real ARIA contract (role + accessible name + aria-modal) on the element.
    const dialog = await screen.findByRole('dialog', { name: /Prompt library/i, hidden: true });
    expect(dialog).toBeInTheDocument();
    expect(dialog).toHaveAttribute('aria-modal', 'true');

    const panel = within(dialog);
    // Category filters live in a labeled group; "All" is selected by default.
    expect(panel.getByRole('group', { name: /Prompt categories/i, hidden: true })).toBeInTheDocument();
    expect(panel.getByRole('button', { name: /🏪 All/i, hidden: true })).toHaveAttribute('aria-pressed', 'true');
    expect(panel.getByRole('button', { name: /🛒 Grocery/i, hidden: true })).toHaveAttribute('aria-pressed', 'false');
  });

  it('filters prompts when a category is selected', async () => {
    const user = userEvent.setup();
    renderLibrary();

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));

    const dialog = await screen.findByRole('dialog', { name: /Prompt library/i, hidden: true });
    const panel = within(dialog);

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
    const dialog = await screen.findByRole('dialog', { name: /Prompt library/i, hidden: true });
    await user.click(within(dialog).getByRole('button', { name: GENERAL_PROMPT, hidden: true }));

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(GENERAL_PROMPT);

    // Panel dismisses after a selection.
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /Prompt library/i, hidden: true })).not.toBeInTheDocument();
    });
  });

  it('is keyboard operable: opens with Enter and dismisses with Escape, restoring focus', async () => {
    const user = userEvent.setup();
    renderLibrary();

    const trigger = screen.getByRole('button', { name: /Prompt ideas/i });
    trigger.focus();
    expect(trigger).toHaveFocus();

    await user.keyboard('{Enter}');
    expect(await screen.findByRole('dialog', { name: /Prompt library/i, hidden: true })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /Prompt library/i, hidden: true })).not.toBeInTheDocument();
    });
    // Focus returns to the trigger for a smooth keyboard flow.
    expect(trigger).toHaveFocus();
  });

  it('disables the trigger when disabled', () => {
    renderLibrary({ disabled: true });
    expect(screen.getByRole('button', { name: /Prompt ideas/i })).toBeDisabled();
  });
});
