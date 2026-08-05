import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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

    // Dialog surface exposes an accessible name.
    const dialog = await screen.findByRole('dialog', { name: /Prompt library/i });
    expect(dialog).toBeInTheDocument();

    // Category filters live in a labeled group; "All" is selected by default.
    expect(screen.getByRole('group', { name: /Prompt categories/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /🏪 All/i })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /🛒 Grocery/i })).toHaveAttribute('aria-pressed', 'false');
  });

  it('filters prompts when a category is selected', async () => {
    const user = userEvent.setup();
    renderLibrary();

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));

    // "All" view surfaces prompts from multiple categories.
    expect(await screen.findByRole('button', { name: GENERAL_PROMPT })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: GROCERY_PROMPT })).toBeInTheDocument();

    // Selecting Grocery narrows the list to that category only.
    await user.click(screen.getByRole('button', { name: /🛒 Grocery/i }));
    expect(screen.getByRole('button', { name: /🛒 Grocery/i })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: GROCERY_PROMPT })).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: GENERAL_PROMPT })).not.toBeInTheDocument();
    });
  });

  it('sends the chosen prompt and closes the panel', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    renderLibrary({ onSelect });

    await user.click(screen.getByRole('button', { name: /Prompt ideas/i }));
    await user.click(await screen.findByRole('button', { name: GENERAL_PROMPT }));

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(GENERAL_PROMPT);

    // Panel dismisses after a selection.
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /Prompt library/i })).not.toBeInTheDocument();
    });
  });

  it('is keyboard operable: opens with Enter and dismisses with Escape, restoring focus', async () => {
    const user = userEvent.setup();
    renderLibrary();

    const trigger = screen.getByRole('button', { name: /Prompt ideas/i });
    trigger.focus();
    expect(trigger).toHaveFocus();

    await user.keyboard('{Enter}');
    expect(await screen.findByRole('dialog', { name: /Prompt library/i })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: /Prompt library/i })).not.toBeInTheDocument();
    });
    // Focus returns to the trigger for a smooth keyboard flow.
    expect(trigger).toHaveFocus();
  });

  it('disables the trigger when disabled', () => {
    renderLibrary({ disabled: true });
    expect(screen.getByRole('button', { name: /Prompt ideas/i })).toBeDisabled();
  });
});
