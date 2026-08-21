import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useActivePack } from '../hooks/useActivePack';
import { PROMPT_CATEGORIES } from '../constants/prompts';

const originalFetch = globalThis.fetch;

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function packPayload(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    key: 'halcyon-pet-supply',
    displayName: 'Halcyon Pet Supply',
    description: 'Fictional specialty pet retailer.',
    version: '1.0.0',
    segment: 'Specialty Retail',
    attribution: 'Fictional sample content.',
    tenant: {
      company: 'Halcyon Pet Supply',
      industry: 'Specialty Pet Retail',
      description: 'Regional pet retailer.',
      brands: [{ name: 'Halcyon Kitchen', category: 'Nutrition', variants: ['Dry', 'Wet'], priceSegment: 'Premium' }],
      regions: ['Great Lakes'],
      channels: ['Store', 'Delivery'],
      theme: { primaryColor: '#123456', accentColor: '#abcdef', logoPath: '', fontFamily: '' },
      distribution: { model: 'Direct', distributorTypes: ['Retailer'] },
    },
    ...overrides,
  };
}

function tasksPayload() {
  return {
    packKey: 'halcyon-pet-supply',
    categories: [
      {
        id: 'nutrition',
        label: 'Nutrition',
        emoji: 'bone',
        prompts: ['How is Halcyon Kitchen wet food trending in the Great Lakes region?'],
      },
    ],
  };
}

describe('useActivePack', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('starts in loading with the built-in prompt categories so the welcome state can render immediately', () => {
    globalThis.fetch = vi.fn().mockImplementation(() => new Promise(() => {})) as unknown as typeof fetch;

    const { result } = renderHook(() => useActivePack());

    expect(result.current.status).toBe('loading');
    expect(result.current.pack).toBeNull();
    expect(result.current.categories).toBe(PROMPT_CATEGORIES);
    expect(result.current.error).toBeNull();
  });

  it('swaps in the pack projection and pack-supplied categories after the fan-out resolves', async () => {
    globalThis.fetch = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/api/pack')) return Promise.resolve(jsonResponse(packPayload()));
      if (url.endsWith('/api/pack/starting-tasks')) return Promise.resolve(jsonResponse(tasksPayload()));
      return Promise.resolve(new Response(null, { status: 404 }));
    }) as unknown as typeof fetch;

    const { result } = renderHook(() => useActivePack());

    await waitFor(() => expect(result.current.status).toBe('ready'));
    expect(result.current.pack?.key).toBe('halcyon-pet-supply');
    expect(result.current.pack?.tenant.industry).toBe('Specialty Pet Retail');
    expect(result.current.pack?.tenant.theme.primaryColor).toBe('#123456');
    expect(result.current.categories.map((c) => c.id)).toEqual(['nutrition']);
  });

  it('falls back to the built-in categories when the pack fetch fails', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(new Response('boom', { status: 500 })) as unknown as typeof fetch;

    const { result } = renderHook(() => useActivePack());

    await waitFor(() => expect(result.current.status).toBe('error'));
    expect(result.current.pack).toBeNull();
    expect(result.current.categories).toBe(PROMPT_CATEGORIES);
    expect(result.current.error).toMatch(/500|Failed to fetch/i);
  });

  it('keeps the built-in categories when the pack loads but starting-tasks returns an empty set', async () => {
    globalThis.fetch = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/api/pack')) return Promise.resolve(jsonResponse(packPayload()));
      if (url.endsWith('/api/pack/starting-tasks')) {
        return Promise.resolve(jsonResponse({ packKey: 'halcyon-pet-supply', categories: [] }));
      }
      return Promise.resolve(new Response(null, { status: 404 }));
    }) as unknown as typeof fetch;

    const { result } = renderHook(() => useActivePack());

    await waitFor(() => expect(result.current.status).toBe('ready'));
    expect(result.current.pack?.key).toBe('halcyon-pet-supply');
    expect(result.current.categories).toBe(PROMPT_CATEGORIES);
  });
});
