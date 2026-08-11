import { useState, useCallback } from 'react';
import {
  Popover,
  PopoverTrigger,
  PopoverSurface,
  Button,
  Text,
  makeStyles,
} from '@fluentui/react-components';
import { Lightbulb24Regular } from '@fluentui/react-icons';
import type { PromptCategory } from '../constants/prompts';

interface PromptLibraryProps {
  categories: ReadonlyArray<PromptCategory>;
  onSelect: (prompt: string) => void;
  disabled?: boolean;
}

const useStyles = makeStyles({
  trigger: {
    flexShrink: 0,
    whiteSpace: 'nowrap',
  },
  surface: {
    padding: '0',
    width: 'min(92vw, 440px)',
    maxWidth: '440px',
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    maxHeight: 'min(70vh, 520px)',
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '14px 16px 10px',
    borderBottom: '1px solid var(--color-border)',
  },
  title: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  subtitle: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
  },
  categoryChips: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
    padding: '12px 16px',
    borderBottom: '1px solid var(--color-border)',
  },
  categoryChip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '6px 12px',
    borderRadius: '16px',
    border: '1px solid var(--color-border)',
    background: 'var(--color-surface)',
    color: 'var(--color-text-muted)',
    cursor: 'pointer',
    fontSize: '12px',
    fontWeight: '500',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'var(--brand-accent-soft)',
      border: '1px solid var(--brand-accent-border)',
      color: 'var(--brand-accent-light)',
    },
    ':focus-visible': {
      outline: '2px solid var(--brand-accent)',
      outlineOffset: '1px',
    },
  },
  categoryChipActive: {
    background: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent)',
    color: 'var(--brand-accent)',
    fontWeight: '600',
  },
  promptList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px 16px 16px',
    overflowY: 'auto',
  },
  promptGroup: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  groupLabel: {
    fontSize: '11px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: 'var(--color-text-muted)',
    margin: '4px 0 2px',
  },
  promptButton: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    color: 'var(--color-text-muted)',
    padding: '10px 12px',
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '13px',
    textAlign: 'left',
    lineHeight: '1.4',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'var(--color-surface-hover)',
      border: '1px solid var(--brand-accent-soft-hover)',
      color: 'var(--brand-accent-light)',
    },
    ':focus-visible': {
      outline: '2px solid var(--brand-accent)',
      outlineOffset: '1px',
    },
    ':disabled': {
      opacity: '0.4',
      cursor: 'not-allowed',
    },
  },
});

/**
 * Always-available, discoverable prompt library rendered as a popover next to
 * the composer. Reuses the shared PROMPT_CATEGORIES source and the caller's safe
 * send behavior so a selected prompt is dispatched exactly like a welcome chip.
 */
export function PromptLibrary({ categories, onSelect, disabled }: PromptLibraryProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [selectedCategory, setSelectedCategory] = useState<string | null>(null);

  const visibleCategories = selectedCategory
    ? categories.filter((c) => c.id === selectedCategory)
    : categories;

  const handlePromptClick = useCallback(
    (prompt: string) => {
      onSelect(prompt);
      setOpen(false);
    },
    [onSelect],
  );

  return (
    <Popover
      open={open}
      onOpenChange={(_, data) => setOpen(data.open)}
      trapFocus
      withArrow
      positioning="above-end"
    >
      <PopoverTrigger disableButtonEnhancement>
        <Button
          className={styles.trigger}
          appearance="subtle"
          icon={<Lightbulb24Regular />}
          disabled={disabled}
          aria-label="Prompt ideas"
          data-testid="prompt-library-trigger"
        >
          Prompt ideas
        </Button>
      </PopoverTrigger>
      <PopoverSurface className={styles.surface} aria-label="Prompt library" data-testid="prompt-library-panel">
        <div className={styles.panel}>
          <div className={styles.header}>
            <Text as="h2" className={styles.title}>
              Prompt library
            </Text>
            <Text className={styles.subtitle}>
              Browse a category and pick a prompt to send.
            </Text>
          </div>
          <div className={styles.categoryChips} role="group" aria-label="Prompt categories" data-testid="prompt-library-categories">
            <button
              type="button"
              className={`${styles.categoryChip} ${selectedCategory === null ? styles.categoryChipActive : ''}`}
              aria-pressed={selectedCategory === null}
              data-testid="prompt-library-category-all"
              onClick={() => setSelectedCategory(null)}
            >
              🏪 All
            </button>
            {categories.map((cat) => (
              <button
                key={cat.id}
                type="button"
                className={`${styles.categoryChip} ${selectedCategory === cat.id ? styles.categoryChipActive : ''}`}
                aria-pressed={selectedCategory === cat.id}
                data-testid={`prompt-library-category-${cat.id}`}
                onClick={() => setSelectedCategory(cat.id)}
              >
                {cat.emoji} {cat.label}
              </button>
            ))}
          </div>
          <div className={styles.promptList} data-testid="prompt-library-list">
            {visibleCategories.map((cat) => (
              <div key={cat.id} className={styles.promptGroup} data-testid={`prompt-library-group-${cat.id}`}>
                {!selectedCategory && (
                  <Text className={styles.groupLabel}>
                    {cat.emoji} {cat.label}
                  </Text>
                )}
                {cat.prompts.map((prompt, i) => (
                  <button
                    key={`${cat.id}-${i}`}
                    type="button"
                    className={styles.promptButton}
                    data-testid="prompt-library-item"
                    data-prompt-category={cat.id}
                    data-prompt-index={i}
                    onClick={() => handlePromptClick(prompt)}
                    disabled={disabled}
                  >
                    {prompt}
                  </button>
                ))}
              </div>
            ))}
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}
