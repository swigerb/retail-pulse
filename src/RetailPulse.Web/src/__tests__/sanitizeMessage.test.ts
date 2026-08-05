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

  describe('chart-spec JSON stripping (screenshot regression)', () => {
    // The exact production failure: the model narrated its CreateChart payload as
    // raw JSON (alternate Chart.js-style schema) at the top of the reply.
    const screenshotJson =
      '{"type":"bar","title":"Consolidation Check 2026-08-05","data":{"labels":["ClearDesk Vodka","Sierra Gold Tequila"],"series":[{"name":"Depletion Velocity","values":[12.5,9.8]}]},"options":{"orientation":"horizontal"}}';

    it('strips a leading chart-spec JSON block from prose', () => {
      const msg = `${screenshotJson}\n\nHere's the depletion velocity comparison for spirits brands in the Northeast.`;
      const result = sanitizeMessage(msg);
      expect(result).not.toContain('"type":"bar"');
      expect(result).not.toContain('"series"');
      expect(result).not.toContain('{');
      expect(result).toBe("Here's the depletion velocity comparison for spirits brands in the Northeast.");
    });

    it('strips a canonical-schema chart-spec JSON block', () => {
      const msg =
        'Here is the chart.\n\n{"type":"line","title":"Trend","data":[{"legend":"BrandA","values":[{"x":"Jan","y":10}]}]}';
      const result = sanitizeMessage(msg);
      expect(result).toBe('Here is the chart.');
    });

    it('strips chart JSON wrapped in a code fence, leaving no empty fence', () => {
      const msg = '```json\n' + screenshotJson + '\n```\n\nProse after the chart.';
      const result = sanitizeMessage(msg);
      expect(result).not.toContain('```');
      expect(result).not.toContain('"labels"');
      expect(result).toContain('Prose after the chart.');
    });

    it('strips the full-config variant (top-level series + xAxis.categories)', () => {
      const fullConfig =
        '{"type":"bar","title":"Depletion Velocity","xAxis":{"label":"Brand","categories":["Sierra Gold","Summit"]},"yAxis":{"label":"Avg Weekly Volume"},"series":[{"name":"Avg Weekly Volume","data":[1893.2,2296.3]}]}';
      const msg = 'Here is the bar chart.\n\n```json\n' + fullConfig + '\n```';
      const result = sanitizeMessage(msg);
      expect(result).not.toContain('```');
      expect(result).not.toContain('"series"');
      expect(result).toBe('Here is the bar chart.');
    });

    it('does not strip non-chart JSON the user may be discussing', () => {
      const msg = 'The payload was {"brand":"Apex Grill","region":"Northeast","velocity":7.2} for reference.';
      expect(sanitizeMessage(msg)).toBe(msg);
    });

    it('does not strip JSON lacking a recognized chart type', () => {
      const msg = 'Config: {"type":"radar","title":"Unsupported","data":[]}';
      // "radar" is not a renderable chart type, so it is surfaced, not hidden.
      expect(sanitizeMessage(msg)).toContain('"type":"radar"');
    });

    it('leaves prose-only chart discussion untouched', () => {
      const msg = 'A bar chart would show depletion velocity by brand across the Northeast region.';
      expect(sanitizeMessage(msg)).toBe(msg);
    });
  });
});
