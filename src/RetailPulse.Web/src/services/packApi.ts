import type { PromptCategory } from '../constants/prompts';
import { resolveApiUrl } from '../config/apiOrigin';
import type {
  PackBrand,
  PackDistribution,
  PackInfo,
  PackStartingTasks,
  PackTenant,
  PackTheme,
} from '../types/pack';

/**
 * Read-only client for the active content pack. The pack endpoints are
 * anonymous and cheap — every response is a projection of the singleton
 * `LoadedPack` the API booted with — so this service intentionally
 * mirrors the small shape of `scorecardApi` / `knowledgeApi`: a plain
 * `fetch` behind `resolveApiUrl`, a status-based error message, and
 * strict normalization at the JSON boundary so downstream React code
 * never receives untyped `unknown` fields.
 */

function readString(source: Record<string, unknown>, key: string): string {
  const value = source[key];
  return typeof value === 'string' ? value : '';
}

function readStringArray(source: Record<string, unknown>, key: string): readonly string[] {
  const value = source[key];
  if (!Array.isArray(value)) return [];
  const out: string[] = [];
  for (const item of value) {
    if (typeof item === 'string') out.push(item);
  }
  return out;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function normalizeTheme(raw: unknown): PackTheme {
  const source = isObject(raw) ? raw : {};
  return {
    primaryColor: readString(source, 'primaryColor'),
    accentColor: readString(source, 'accentColor'),
    logoPath: readString(source, 'logoPath'),
    fontFamily: readString(source, 'fontFamily'),
  };
}

function normalizeDistribution(raw: unknown): PackDistribution {
  const source = isObject(raw) ? raw : {};
  return {
    model: readString(source, 'model'),
    distributorTypes: readStringArray(source, 'distributorTypes'),
  };
}

function normalizeBrand(raw: unknown): PackBrand | null {
  if (!isObject(raw)) return null;
  const name = readString(raw, 'name');
  if (!name) return null;
  return {
    name,
    category: readString(raw, 'category'),
    variants: readStringArray(raw, 'variants'),
    priceSegment: readString(raw, 'priceSegment'),
  };
}

function normalizeBrands(raw: unknown): readonly PackBrand[] {
  if (!Array.isArray(raw)) return [];
  const out: PackBrand[] = [];
  for (const item of raw) {
    const brand = normalizeBrand(item);
    if (brand) out.push(brand);
  }
  return out;
}

function normalizeTenant(raw: unknown): PackTenant {
  const source = isObject(raw) ? raw : {};
  return {
    company: readString(source, 'company'),
    industry: readString(source, 'industry'),
    description: readString(source, 'description'),
    brands: normalizeBrands(source.brands),
    regions: readStringArray(source, 'regions'),
    channels: readStringArray(source, 'channels'),
    theme: normalizeTheme(source.theme),
    distribution: normalizeDistribution(source.distribution),
  };
}

function normalizePackInfo(raw: unknown): PackInfo {
  const source = isObject(raw) ? raw : {};
  const key = readString(source, 'key');
  if (!key) {
    throw new Error('Pack response is missing a key');
  }
  return {
    key,
    displayName: readString(source, 'displayName'),
    description: readString(source, 'description'),
    version: readString(source, 'version'),
    segment: readString(source, 'segment'),
    attribution: readString(source, 'attribution'),
    tenant: normalizeTenant(source.tenant),
  };
}

function normalizeCategory(raw: unknown): PromptCategory | null {
  if (!isObject(raw)) return null;
  const id = readString(raw, 'id');
  const label = readString(raw, 'label');
  const emoji = readString(raw, 'emoji');
  const prompts = readStringArray(raw, 'prompts');
  if (!id || !label || prompts.length === 0) return null;
  return { id, label, emoji, prompts: [...prompts] };
}

function normalizeStartingTasks(raw: unknown): PackStartingTasks {
  const source = isObject(raw) ? raw : {};
  const packKey = readString(source, 'packKey');
  const rawCategories = source.categories;
  const categories: PromptCategory[] = [];
  if (Array.isArray(rawCategories)) {
    for (const item of rawCategories) {
      const cat = normalizeCategory(item);
      if (cat) categories.push(cat);
    }
  }
  return { packKey, categories };
}

export async function fetchActivePack(signal?: AbortSignal): Promise<PackInfo> {
  const res = await fetch(resolveApiUrl('/api/pack'), { signal });
  if (!res.ok) {
    throw new Error(`Failed to fetch active pack: ${res.status}`);
  }
  const raw: unknown = await res.json();
  return normalizePackInfo(raw);
}

export async function fetchStartingTasks(signal?: AbortSignal): Promise<PackStartingTasks> {
  const res = await fetch(resolveApiUrl('/api/pack/starting-tasks'), { signal });
  if (!res.ok) {
    throw new Error(`Failed to fetch starting tasks: ${res.status}`);
  }
  const raw: unknown = await res.json();
  return normalizeStartingTasks(raw);
}
