import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fetchActivePack, fetchStartingTasks } from '../services/packApi';

const originalFetch = globalThis.fetch;

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

describe('packApi.fetchActivePack', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllEnvs();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('parses the pack projection and its nested tenant + theme + brands', async () => {
    const payload = {
      key: 'default',
      displayName: 'Apex Retail Group (Sample Tenant)',
      description: 'A fictional multi-category retail conglomerate.',
      version: '1.0.0',
      segment: 'Multi-Category Retail',
      attribution: 'Fictional sample content.',
      tenant: {
        company: 'Apex Retail Group',
        industry: 'Multi-Category Retail',
        description: 'Diversified retail conglomerate.',
        brands: [
          { name: 'FreshMart', category: 'Grocery', variants: ['Bakery', 'Deli'], priceSegment: 'Standard' },
        ],
        regions: ['Northeast', 'West Coast'],
        channels: ['On-Premise', 'E-Commerce'],
        theme: {
          primaryColor: '#1B4D7A',
          accentColor: '#E8A838',
          logoPath: 'assets/apex-logo.png',
          fontFamily: 'Inter, system-ui, sans-serif',
        },
        distribution: { model: 'Three-Tier', distributorTypes: ['Distributor', 'Retailer'] },
      },
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const pack = await fetchActivePack();

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/pack', expect.any(Object));
    expect(pack.key).toBe('default');
    expect(pack.displayName).toBe('Apex Retail Group (Sample Tenant)');
    expect(pack.tenant.company).toBe('Apex Retail Group');
    expect(pack.tenant.industry).toBe('Multi-Category Retail');
    expect(pack.tenant.regions).toEqual(['Northeast', 'West Coast']);
    expect(pack.tenant.brands).toEqual([
      { name: 'FreshMart', category: 'Grocery', variants: ['Bakery', 'Deli'], priceSegment: 'Standard' },
    ]);
    expect(pack.tenant.theme.primaryColor).toBe('#1B4D7A');
    expect(pack.tenant.theme.accentColor).toBe('#E8A838');
    expect(pack.tenant.distribution.model).toBe('Three-Tier');
    expect(pack.tenant.distribution.distributorTypes).toEqual(['Distributor', 'Retailer']);
  });

  it('routes to the configured direct ACA origin when VITE_API_ORIGIN is set', async () => {
    vi.stubEnv('VITE_API_ORIGIN', 'https://api.example.test');
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse({ key: 'default', tenant: { theme: {}, distribution: {} } }),
    ) as unknown as typeof fetch;

    await fetchActivePack();

    expect(globalThis.fetch).toHaveBeenCalledWith('https://api.example.test/api/pack', expect.any(Object));
  });

  it('defensively fills tenant, theme, and distribution when the response omits them', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse({ key: 'lean', displayName: 'Lean Pack' }),
    ) as unknown as typeof fetch;

    const pack = await fetchActivePack();

    expect(pack.key).toBe('lean');
    expect(pack.displayName).toBe('Lean Pack');
    expect(pack.tenant.company).toBe('');
    expect(pack.tenant.regions).toEqual([]);
    expect(pack.tenant.brands).toEqual([]);
    expect(pack.tenant.theme).toEqual({
      primaryColor: '',
      accentColor: '',
      logoPath: '',
      fontFamily: '',
    });
    expect(pack.tenant.distribution).toEqual({ model: '', distributorTypes: [] });
  });

  it('rejects a response with no pack key so the caller falls back to defaults', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse({ displayName: 'nameless' }),
    ) as unknown as typeof fetch;

    await expect(fetchActivePack()).rejects.toThrow(/missing a key/i);
  });

  it('throws with the status when the endpoint responds non-2xx', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('boom', { status: 503 }),
    ) as unknown as typeof fetch;

    await expect(fetchActivePack()).rejects.toThrow(/503/);
  });

  it('propagates AbortError from a caller signal', async () => {
    const controller = new AbortController();
    globalThis.fetch = vi.fn().mockImplementation(
      (_input: unknown, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            reject(new DOMException('Aborted', 'AbortError'));
          });
        }),
    ) as unknown as typeof fetch;

    const promise = fetchActivePack(controller.signal);
    controller.abort();
    await expect(promise).rejects.toThrow(/Aborted|abort/i);
  });
});

describe('packApi.fetchStartingTasks', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('parses categories and drops entries that are missing required fields', async () => {
    const payload = {
      packKey: 'default',
      categories: [
        {
          id: 'general',
          label: 'General Retail',
          emoji: 'chart',
          prompts: ['Compare depletion trends across all regions for this quarter'],
        },
        // Missing id — must be silently dropped so a malformed entry does not
        // break the whole panel.
        { label: 'Broken', emoji: 'x', prompts: ['no-id'] },
        // Missing prompts — must also be dropped for the same reason.
        { id: 'empty', label: 'Empty', emoji: 'x', prompts: [] },
        {
          id: 'grocery',
          label: 'Grocery',
          emoji: 'cart',
          prompts: ['How are FreshMart depletions trending in the Northeast this quarter?'],
        },
      ],
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/pack/starting-tasks', expect.any(Object));
    expect(tasks.packKey).toBe('default');
    expect(tasks.categories).toHaveLength(2);
    expect(tasks.categories.map((c) => c.id)).toEqual(['general', 'grocery']);
    expect(tasks.categories[0].prompts).toEqual([
      'Compare depletion trends across all regions for this quarter',
    ]);
  });

  it('returns empty categories when the response omits them entirely', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      jsonResponse({ packKey: 'lean' }),
    ) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();

    expect(tasks.packKey).toBe('lean');
    expect(tasks.categories).toEqual([]);
  });

  it('throws with the status when the endpoint responds non-2xx', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(
      new Response('nope', { status: 500 }),
    ) as unknown as typeof fetch;

    await expect(fetchStartingTasks()).rejects.toThrow(/500/);
  });

  it('parses the issue #109 tasks[] shape and preserves display name + submitted prompt + capability', async () => {
    const payload = {
      packKey: 'halcyon-pet-supply',
      categories: [
        {
          id: 'nutrition',
          label: 'Nutrition',
          emoji: '🥣',
          order: 1,
          tasks: [
            {
              name: 'Auto-ship depletion trend',
              prompt: 'How is Meadowbowl Nutrition auto-ship depletion trending in the Sunbelt this quarter?',
              order: 1,
              capability: { kind: 'prose' },
            },
            {
              name: 'Grain-inclusive vs grain-free share',
              prompt: 'Compare grain-inclusive vs grain-free share for Riverstone Feline across all regions',
              order: 2,
              capability: { kind: 'chart', chartType: 'bar' },
            },
            {
              name: 'Life-stage forecast',
              prompt: 'Show a life-stage transition forecast for puppy → adult across the Great Lakes',
              order: 3,
              capability: { kind: 'plan', planPath: 'multi-step-forecast' },
            },
          ],
        },
      ],
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();
    expect(tasks.categories).toHaveLength(1);

    const cat = tasks.categories[0];
    expect(cat.order).toBe(1);
    expect(cat.tasks).toHaveLength(3);
    expect(cat.tasks[0].name).toBe('Auto-ship depletion trend');
    expect(cat.tasks[0].prompt).toContain('Meadowbowl Nutrition');
    expect(cat.tasks[0].capability).toEqual({ kind: 'prose' });
    expect(cat.tasks[1].capability).toEqual({ kind: 'chart', chartType: 'bar' });
    expect(cat.tasks[2].capability).toEqual({ kind: 'plan', planPath: 'multi-step-forecast' });

    // Derived `prompts` array preserves submitted-prompt order for the
    // legacy shape consumers.
    expect(cat.prompts).toEqual(cat.tasks.map((t) => t.prompt));
  });

  it('sorts categories and tasks by their explicit `order` regardless of source-array position', async () => {
    const payload = {
      packKey: 'ordering',
      categories: [
        {
          id: 'second-in-yaml',
          label: 'Second in YAML',
          emoji: 'b',
          order: 1,
          tasks: [
            { name: 'Late task', prompt: 'p-late', order: 5 },
            { name: 'Early task', prompt: 'p-early', order: 1 },
          ],
        },
        {
          id: 'first-in-yaml',
          label: 'First in YAML',
          emoji: 'a',
          order: 10,
          tasks: [{ name: 'Solo', prompt: 'p-solo' }],
        },
      ],
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();
    expect(tasks.categories.map((c) => c.id)).toEqual(['second-in-yaml', 'first-in-yaml']);
    expect(tasks.categories[0].tasks.map((t) => t.name)).toEqual(['Early task', 'Late task']);
  });

  it('prefers structured tasks[] over legacy prompts[] when both are present', async () => {
    const payload = {
      packKey: 'both',
      categories: [
        {
          id: 'mixed',
          label: 'Mixed',
          emoji: 'm',
          tasks: [{ name: 'From tasks', prompt: 'submitted-tasks-prompt' }],
          prompts: ['legacy-prompt-should-be-ignored'],
        },
      ],
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();
    expect(tasks.categories[0].tasks).toHaveLength(1);
    expect(tasks.categories[0].tasks[0].name).toBe('From tasks');
    expect(tasks.categories[0].tasks[0].prompt).toBe('submitted-tasks-prompt');
    expect(tasks.categories[0].prompts).toEqual(['submitted-tasks-prompt']);
  });

  it('drops malformed tasks (missing name or prompt) and unknown capability kinds', async () => {
    const payload = {
      packKey: 'broken',
      categories: [
        {
          id: 'mostly-fine',
          label: 'Mostly Fine',
          emoji: 'x',
          tasks: [
            { name: 'Good', prompt: 'good-prompt', capability: { kind: 'chart', chartType: 'line' } },
            { name: '', prompt: 'no-name' },
            { name: 'No prompt', prompt: '' },
            { name: 'Unknown kind', prompt: 'weird', capability: { kind: 'sorcery' } },
          ],
        },
      ],
    };

    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(payload)) as unknown as typeof fetch;

    const tasks = await fetchStartingTasks();
    expect(tasks.categories[0].tasks).toHaveLength(2);
    expect(tasks.categories[0].tasks[0].name).toBe('Good');
    expect(tasks.categories[0].tasks[0].capability).toEqual({ kind: 'chart', chartType: 'line' });
    // The 'Unknown kind' task survives because name+prompt are valid, but its
    // capability is dropped (defensive normalization at the JSON boundary).
    expect(tasks.categories[0].tasks[1].name).toBe('Unknown kind');
    expect(tasks.categories[0].tasks[1].capability).toBeUndefined();
  });
});
