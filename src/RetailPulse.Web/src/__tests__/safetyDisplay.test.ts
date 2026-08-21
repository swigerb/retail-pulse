import { describe, it, expect } from 'vitest';
import type { BlockedRequest } from '../types';
import {
  aggregateByCategory,
  aggregateByFamily,
  aggregateBySeverity,
  buildSafetyBlockDisplay,
  buildSafetyDisplayFromBlockedRequest,
  classifyBlockFamily,
  describeCategory,
  describeSeverity,
  detectSafetyRefusal,
  normaliseCategoryName,
} from '../utils/safetyDisplay';

describe('classifyBlockFamily', () => {
  it.each(['jailbreak', 'pii', 'access'] as const)('classifies %s as pattern-based', t => {
    expect(classifyBlockFamily(t)).toBe('pattern');
  });

  it.each([
    'content-safety-hate',
    'content-safety-sexual',
    'content-safety-violence',
    'content-safety-selfharm',
    'content-safety-prompt-shield',
    'content-safety-indirect-injection',
    'content-safety-unavailable',
  ] as const)('classifies %s as model-based', t => {
    expect(classifyBlockFamily(t)).toBe('model');
  });

  it('falls back to model for future content-safety-* types via prefix', () => {
    expect(classifyBlockFamily('content-safety-future-signal')).toBe('model');
  });

  it('returns unknown for empty/unrecognised strings', () => {
    expect(classifyBlockFamily(null)).toBe('unknown');
    expect(classifyBlockFamily(undefined)).toBe('unknown');
    expect(classifyBlockFamily('mystery')).toBe('unknown');
  });
});

describe('describeSeverity', () => {
  it('buckets Content Safety severities into plain-language labels', () => {
    expect(describeSeverity(0)).toBe('low');
    expect(describeSeverity(2)).toBe('medium');
    expect(describeSeverity(4)).toBe('high');
    expect(describeSeverity(6)).toBe('severe');
    expect(describeSeverity(undefined)).toBeUndefined();
  });
});

describe('normaliseCategoryName / describeCategory', () => {
  it('normalises variant spellings of self-harm', () => {
    expect(normaliseCategoryName('SelfHarm')).toBe('SelfHarm');
    expect(normaliseCategoryName('self-harm')).toBe('SelfHarm');
    expect(normaliseCategoryName('SELF HARM')).toBe('SelfHarm');
  });

  it('describes categories in plain language', () => {
    expect(describeCategory('Hate')).toBe('Hateful content');
    expect(describeCategory(undefined, 'content-safety-violence')).toBe('Violent content');
    expect(describeCategory(undefined, 'jailbreak')).toBe('Prompt jailbreak');
  });

  it('never returns a raw category code', () => {
    const label = describeCategory('SelfHarm', 'content-safety-selfharm');
    expect(label).toBeDefined();
    expect(label).not.toMatch(/selfharm/i);
    expect(label).not.toMatch(/content-safety/i);
  });
});

describe('buildSafetyBlockDisplay', () => {
  it('produces a whitelisted model with plain-language reason when no override is given', () => {
    const d = buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-hate',
      category: 'Hate',
      severity: 4,
      decision: 'Blocked',
    });
    expect(d.family).toBe('model');
    expect(d.modelBased).toBe(true);
    expect(d.categoryLabel).toBe('Hateful content');
    expect(d.severityLabel).toBe('high');
    expect(d.decision).toBe('Blocked');
    expect(d.reason).toMatch(/blocked/i);
    // Guarantee: severity number never appears in the rendered reason.
    expect(d.reason).not.toMatch(/\b4\b/);
  });

  it('renders a specific reason for the fail-closed unavailable case', () => {
    const d = buildSafetyBlockDisplay({
      stage: 'input',
      detectionType: 'content-safety-unavailable',
      decision: 'ServiceUnavailable',
      failClosed: true,
    });
    expect(d.decision).toBe('ServiceUnavailable');
    expect(d.reason).toMatch(/fail closed/i);
    expect(d.failClosed).toBe(true);
  });
});

describe('buildSafetyDisplayFromBlockedRequest', () => {
  it('maps a log entry to a whitelisted display model', () => {
    const entry: BlockedRequest = {
      id: '1',
      timestamp: '2026-08-20T00:00:00Z',
      requestPreview: 'irrelevant preview text',
      detectionType: 'content-safety-sexual',
      reason: 'irrelevant reason',
      actionTaken: 'blocked',
      category: 'Sexual',
      severity: 6,
      decision: 'Blocked',
    };
    const d = buildSafetyDisplayFromBlockedRequest(entry);
    expect(d.family).toBe('model');
    expect(d.categoryLabel).toBe('Sexual content');
    expect(d.severityLabel).toBe('severe');
  });
});

describe('detectSafetyRefusal', () => {
  it('recognises the default guardrails refusal (content safety)', () => {
    const model = detectSafetyRefusal(
      "I can't help with that request. My guardrails detected potentially harmful content (content safety). Please rephrase your question about retail operations and I'll be happy to assist.",
    );
    expect(model).not.toBeNull();
    expect(model?.family).toBe('model');
    expect(model?.decision).toBe('Blocked');
  });

  it('recognises the default guardrails refusal (jailbreak attempt)', () => {
    const model = detectSafetyRefusal(
      "I can't help with that request. My guardrails detected potentially harmful content (jailbreak attempt). Please rephrase your question about retail operations and I'll be happy to assist.",
    );
    expect(model).not.toBeNull();
    expect(model?.family).toBe('pattern');
  });

  it('recognises the fail-closed unavailable refusal', () => {
    const model = detectSafetyRefusal(
      "I can't process that request right now. The content safety layer is temporarily unavailable and this deployment is configured to fail closed. Please retry shortly.",
    );
    expect(model?.decision).toBe('ServiceUnavailable');
    expect(model?.failClosed).toBe(true);
  });

  it('returns null for a normal reply', () => {
    expect(detectSafetyRefusal('Here are your Q3 sales numbers.')).toBeNull();
    expect(detectSafetyRefusal('')).toBeNull();
  });

  it('never carries the raw refusal-type substring beyond the whitelisted mapping', () => {
    const model = detectSafetyRefusal(
      "I can't help with that request. My guardrails detected potentially harmful content (SENSITIVE_PATTERN_XYZ). Please rephrase.",
    );
    expect(model).not.toBeNull();
    // Unknown refusal type must fall back to the prompt-shield default,
    // never carry the raw substring into the display model.
    expect(model?.reason).not.toContain('SENSITIVE_PATTERN_XYZ');
    expect(model?.categoryLabel ?? '').not.toContain('SENSITIVE_PATTERN_XYZ');
  });
});

describe('aggregators', () => {
  const entries: BlockedRequest[] = [
    { id: '1', timestamp: '', requestPreview: '', detectionType: 'jailbreak', reason: '', actionTaken: '' },
    { id: '2', timestamp: '', requestPreview: '', detectionType: 'pii', reason: '', actionTaken: '' },
    { id: '3', timestamp: '', requestPreview: '', detectionType: 'content-safety-hate', reason: '', actionTaken: '', category: 'Hate', severity: 4 },
    { id: '4', timestamp: '', requestPreview: '', detectionType: 'content-safety-violence', reason: '', actionTaken: '', category: 'Violence', severity: 6 },
    { id: '5', timestamp: '', requestPreview: '', detectionType: 'content-safety-sexual', reason: '', actionTaken: '', category: 'Sexual', severity: 2 },
  ];

  it('splits pattern vs model families', () => {
    expect(aggregateByFamily(entries)).toEqual({ pattern: 2, model: 3 });
  });

  it('aggregates categories from model-family entries only', () => {
    const byCat = aggregateByCategory(entries);
    const asMap = Object.fromEntries(byCat.map(a => [a.category, a.count]));
    expect(asMap.Hate).toBe(1);
    expect(asMap.Sexual).toBe(1);
    expect(asMap.Violence).toBe(1);
    expect(asMap.SelfHarm).toBe(0);
  });

  it('aggregates severities into buckets for model entries only', () => {
    const bySev = aggregateBySeverity(entries);
    const asMap = Object.fromEntries(bySev.map(a => [a.bucket, a.count]));
    expect(asMap.medium).toBe(1);
    expect(asMap.high).toBe(1);
    expect(asMap.severe).toBe(1);
    // Pattern entries are excluded from severity aggregation.
    expect(asMap.low + asMap.medium + asMap.high + asMap.severe).toBe(3);
  });
});
