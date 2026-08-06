const API_PREFIX = '/api/';

/**
 * Accepts only a bare HTTP(S) origin. A malformed build-time value fails safe
 * and callers retain the same-origin SWA route.
 */
export function resolveApiOrigin(value: string | undefined): string | null {
  const candidate = value?.trim();
  if (!candidate) return null;

  try {
    const parsed = new URL(candidate);
    if (
      (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') ||
      parsed.username ||
      parsed.password ||
      (parsed.pathname !== '/' && parsed.pathname !== '') ||
      parsed.search ||
      parsed.hash
    ) {
      return null;
    }
    return parsed.origin;
  } catch {
    return null;
  }
}

export function resolveApiUrl(
  path: string,
  configuredOrigin: string | undefined = import.meta.env.VITE_API_ORIGIN,
): string {
  if (!path.startsWith(API_PREFIX)) {
    throw new Error(`API path must start with ${API_PREFIX}`);
  }
  const origin = resolveApiOrigin(configuredOrigin);
  return origin ? `${origin}${path}` : path;
}
