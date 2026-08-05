import { describe, it, expect } from 'vitest';
import { resolveTelemetryHubUrl } from '../config/telemetryHubUrl';

describe('resolveTelemetryHubUrl', () => {
  it('returns the relative path when origin is undefined', () => {
    expect(resolveTelemetryHubUrl(undefined)).toBe('/hubs/telemetry');
  });

  it('returns the relative path when origin is an empty string', () => {
    expect(resolveTelemetryHubUrl('')).toBe('/hubs/telemetry');
  });

  it('returns the relative path when origin is whitespace only', () => {
    expect(resolveTelemetryHubUrl('   ')).toBe('/hubs/telemetry');
  });

  it('resolves an absolute hub URL when origin is configured', () => {
    expect(resolveTelemetryHubUrl('https://api.example.com')).toBe(
      'https://api.example.com/hubs/telemetry',
    );
  });

  it('normalizes a single trailing slash on the origin', () => {
    expect(resolveTelemetryHubUrl('https://api.example.com/')).toBe(
      'https://api.example.com/hubs/telemetry',
    );
  });

  it('normalizes multiple trailing slashes on the origin', () => {
    expect(resolveTelemetryHubUrl('https://api.example.com///')).toBe(
      'https://api.example.com/hubs/telemetry',
    );
  });

  it('trims surrounding whitespace before resolving', () => {
    expect(resolveTelemetryHubUrl('  https://api.example.com/  ')).toBe(
      'https://api.example.com/hubs/telemetry',
    );
  });
});
