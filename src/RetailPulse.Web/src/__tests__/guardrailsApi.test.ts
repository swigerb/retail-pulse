import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fetchGuardrailsLog, updateGuardrailsConfig } from '../services/guardrailsApi';
import type { GuardrailsConfigData } from '../types';

function baseConfig(overrides?: Partial<GuardrailsConfigData>): GuardrailsConfigData {
  return {
    piiDetectionEnabled: true,
    jailbreakDetectionEnabled: true,
    autoRedactPii: true,
    maxInputLength: 10000,
    piiPatterns: ['SSN', 'Email'],
    jailbreakPatterns: ['IgnoreInstructions'],
    contentSafety: {
      enabled: true,
      failPolicy: 'FailClosed',
      promptShieldsEnabled: true,
      checkInput: true,
      checkOutput: true,
      checkRetrievedKnowledge: true,
      checkToolResults: true,
      hateThreshold: 4,
      sexualThreshold: 4,
      violenceThreshold: 4,
      selfHarmThreshold: 4,
    },
    ...overrides,
  };
}

describe('guardrailsApi config contract', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('sends only backend config fields on update', async () => {
    const nextConfig = baseConfig({ jailbreakDetectionEnabled: false });
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_input, init) => {
      const body = JSON.parse(String(init?.body));
      expect(body).toMatchObject({
        piiDetectionEnabled: true,
        jailbreakDetectionEnabled: false,
        autoRedactPii: true,
        maxInputLength: 10000,
      });
      expect(body).not.toHaveProperty('jailbreakEnabled');
      expect(body).not.toHaveProperty('piiEnabled');
      expect(body).not.toHaveProperty('accessControlEnabled');
      expect(body).not.toHaveProperty('blockedPatterns');
      return {
        ok: true,
        json: async () => nextConfig,
      } as Response;
    });

    await expect(updateGuardrailsConfig(nextConfig)).resolves.toMatchObject({
      jailbreakDetectionEnabled: false,
    });
  });

  it('rejects a save when the returned config does not match the requested value', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: async () => baseConfig({ jailbreakDetectionEnabled: true }),
    } as Response);

    await expect(updateGuardrailsConfig(baseConfig({ jailbreakDetectionEnabled: false })))
      .rejects.toThrow(/jailbreakDetectionEnabled/);
  });

  it('maps audit row detail fields from the log endpoint', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      json: async () => [
        {
          id: 'row-1',
          timestamp: '2026-08-29T18:19:24Z',
          requestText: "Tool result from 'GetStorePerformance' blocked by Content Safety",
          detectionType: 'content-safety-sexual',
          action: 'blocked',
          reason: 'Content Safety classified the tool result as Sexual content at severity 4, which met threshold 4.',
          category: 'Sexual',
          severity: 4,
          decision: 'Blocked',
          stage: 'ToolResult',
          threshold: 4,
        },
      ],
    } as Response);

    await expect(fetchGuardrailsLog(1)).resolves.toEqual([
      expect.objectContaining({
        requestPreview: "Tool result from 'GetStorePerformance' blocked by Content Safety",
        actionTaken: 'blocked',
        reason: 'Content Safety classified the tool result as Sexual content at severity 4, which met threshold 4.',
        category: 'Sexual',
        severity: 4,
        decision: 'Blocked',
        stage: 'ToolResult',
        threshold: 4,
      }),
    ]);
  });
});
