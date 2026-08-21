import { useEffect, useState } from 'react';
import { PROMPT_CATEGORIES, type PromptCategory } from '../constants/prompts';
import { fetchActivePack, fetchStartingTasks } from '../services/packApi';
import type { PackInfo } from '../types/pack';

/**
 * Loads the active content pack for the current app boot.
 *
 * Callers get an eager, synchronously-available default so the welcome
 * chips and Prompt Library keep rendering with `PROMPT_CATEGORIES` while
 * the network round-trip resolves — mirroring the pre-#108 baseline
 * behavior — and then swap to the pack-supplied prompts and tenant/theme
 * once the fetch succeeds. A failed load is intentionally silent (only
 * surfaced through `status: 'error'`): the app must remain usable when
 * the pack endpoints are unreachable, and the default categories are a
 * safe presentation.
 */

export type ActivePackStatus = 'loading' | 'ready' | 'error';

export interface ActivePackState {
  readonly status: ActivePackStatus;
  readonly pack: PackInfo | null;
  readonly categories: readonly PromptCategory[];
  readonly error: string | null;
}

const INITIAL_STATE: ActivePackState = {
  status: 'loading',
  pack: null,
  categories: PROMPT_CATEGORIES,
  error: null,
};

export function useActivePack(): ActivePackState {
  const [state, setState] = useState<ActivePackState>(INITIAL_STATE);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      try {
        const [pack, startingTasks] = await Promise.all([
          fetchActivePack(controller.signal),
          fetchStartingTasks(controller.signal),
        ]);
        if (cancelled) return;
        const categories = startingTasks.categories.length > 0
          ? startingTasks.categories
          : PROMPT_CATEGORIES;
        setState({ status: 'ready', pack, categories, error: null });
      } catch (err) {
        if (cancelled) return;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        const message = err instanceof Error ? err.message : 'Failed to load active pack';
        setState({ status: 'error', pack: null, categories: PROMPT_CATEGORIES, error: message });
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, []);

  return state;
}
