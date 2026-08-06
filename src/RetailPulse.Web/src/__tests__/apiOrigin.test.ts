import { describe, expect, it } from 'vitest';
import { resolveApiOrigin, resolveApiUrl } from '../config/apiOrigin';

describe('API origin resolution', () => {
  it('builds a direct API URL from a bare trusted origin', () => {
    expect(resolveApiUrl('/api/chat', 'https://api.example.test/'))
      .toBe('https://api.example.test/api/chat');
  });

  it.each([
    'https://user@api.example.test',
    'https://api.example.test/path',
    'https://api.example.test?target=other',
    'javascript:alert(1)',
    'not a URL',
  ])('rejects malformed or non-origin values: %s', value => {
    expect(resolveApiOrigin(value)).toBeNull();
    expect(resolveApiUrl('/api/chat', value)).toBe('/api/chat');
  });

  it('rejects non-API paths', () => {
    expect(() => resolveApiUrl('/hubs/telemetry', 'https://api.example.test'))
      .toThrow('/api/');
  });
});
