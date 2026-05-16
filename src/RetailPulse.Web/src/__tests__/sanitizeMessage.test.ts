import { describe, it, expect } from 'vitest';
import { sanitizeMessage } from '../utils/sanitizeMessage';

describe('sanitizeMessage', () => {
  it('returns empty string for falsy input', () => {
    expect(sanitizeMessage('')).toBe('');
  });

  it('passes through clean messages unchanged', () => {
    const msg = 'The demand forecast for Apex Grill shows a 12% increase.';
    expect(sanitizeMessage(msg)).toBe(msg);
  });

  it('strips to=functions.* prefix patterns', () => {
    const msg = 'to=functions.IdentifyDemandRisks Some actual content here.';
    expect(sanitizeMessage(msg)).toBe('Some actual content here.');
  });

  it('strips full tool-call blocks with JSON payload', () => {
    const msg = 'to=functions.IdentifyDemandRisks json {"brand":"Apex Grill","horizon":"30d"}\n\nHere is the actual response.';
    expect(sanitizeMessage(msg)).toBe('Here is the actual response.');
  });

  it('strips garbled Unicode characters in tool context', () => {
    const msg = '天天中彩票提現 福利彩票天天彩json {"brand":"Apex Grill"}\n\nThe demand analysis shows growth.';
    expect(sanitizeMessage(msg)).toBe('The demand analysis shows growth.');
  });

  it('handles multiple tool-call fragments in one message', () => {
    const msg = 'to=functions.GetHistoricalDemand {"sku":"abc"}\nto=functions.GetSeasonalityFactors {"region":"NE"}\n\nBased on the analysis, demand is up.';
    const result = sanitizeMessage(msg);
    expect(result).not.toContain('to=functions');
    expect(result).toContain('Based on the analysis, demand is up.');
  });

  it('does not strip legitimate content containing the word "functions"', () => {
    const msg = 'The system uses Azure Functions for serverless compute.';
    expect(sanitizeMessage(msg)).toBe(msg);
  });

  it('collapses excessive blank lines after stripping', () => {
    const msg = 'to=functions.Foo bar\n\n\n\n\nActual content.';
    const result = sanitizeMessage(msg);
    expect(result).not.toMatch(/\n{3,}/);
    expect(result).toContain('Actual content.');
  });
});
