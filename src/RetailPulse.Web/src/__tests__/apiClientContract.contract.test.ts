import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchActiveCards } from '../services/cardsApi';
import { fetchGuardrailsLog, fetchGuardrailsStats } from '../services/guardrailsApi';
import { fetchScorecard } from '../services/operationsApi';

/**
 * Client/API contract, consumer side.
 *
 * Each case loads a JSON fixture that the C# suite produced by serialising the
 * real response DTO with the API's own JSON options
 * (tests/RetailPulse.Tests/ContractFixtures/ApiClientContractFixtureTests.cs), then
 * runs the actual client service/mapper against it and asserts every field the SPA
 * reads is present and correctly typed. Because both suites assert the same
 * committed files, a field renamed, removed, or retyped on either side breaks a
 * test that names the endpoint and the field:
 *
 *  - API stops sending a field -> the C# fixture changes -> the C# snapshot test
 *    fails until the fixture is regenerated; once it is, the mapper below produces
 *    an empty/undefined value and this test fails until the client is updated.
 *    Where a mapper substitutes a default for a missing field, assert the value
 *    against the fixture rather than only its type: a default turns a dropped
 *    field into a plausible zero, which is the failure a type check cannot see.
 *  - Client starts reading a different wire field -> the mapper reads something the
 *    fixture (the real API shape) does not contain -> this test fails here.
 *
 * No server and no Azure: the fixtures are static files, so this runs on every PR.
 */

// Walk up to the repository root (the directory holding RetailPulse.slnx) so the
// fixtures resolve regardless of the vitest working directory.
function repositoryRoot(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let depth = 0; depth < 12; depth += 1) {
    try {
      readFileSync(join(dir, 'RetailPulse.slnx'));
      return dir;
    } catch {
      const parent = dirname(dir);
      if (parent === dir) break;
      dir = parent;
    }
  }
  throw new Error('Could not locate the repository root (no RetailPulse.slnx above this test).');
}

function loadFixture(name: string): unknown {
  const path = join(repositoryRoot(), 'contracts', 'fixtures', `${name}.json`);
  return JSON.parse(readFileSync(path, 'utf8'));
}

function mockFetchJson(payload: unknown): void {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue({
    ok: true,
    status: 200,
    json: async () => payload,
  } as Response);
}

// The panel keys its style maps by name; the wire carries integers. Mirrors the
// C# CardType / CardLifecycle declaration order.
const CARD_TYPES = ['voting', 'drilldown', 'dashboard', 'briefing'] as const;
const CARD_LIFECYCLES = ['active', 'voting', 'decided', 'archived'] as const;
const SCORECARD_DIMENSION_KEYS = ['demand', 'competitive', 'supply', 'store', 'margin'] as const;

afterEach(() => {
  vi.restoreAllMocks();
});

describe('contract: GET /api/guardrails/stats -> GuardrailsStats', () => {
  it('exposes every counter the security dashboard reads', async () => {
    const fixture = loadFixture('guardrails-stats') as Record<string, unknown>;
    mockFetchJson(fixture);

    const stats = await fetchGuardrailsStats();

    const counters = [
      'totalBlocked',
      'jailbreakAttempts',
      'piiDetections',
      'accessDenials',
      'contentSafetyBlocks',
      'contentSafetyFlags',
      'failOpenPasses',
    ] as const;
    for (const field of counters) {
      expect(typeof stats[field], `guardrails/stats.${field} must be a number the client can read`).toBe('number');
      // Assert the value carried through, not merely that it is numeric. The mapper
      // defaults failOpenPasses to 0 when absent, so a type-only assertion would still
      // pass after the API renamed or dropped the field and the counter would silently
      // read zero on the Security page. Comparing against the fixture value is what
      // makes the default visible instead of protective.
      expect(stats[field], `guardrails/stats.${field} must carry the API value, not a client default`)
        .toBe(fixture[field] as number);
    }
    expect(Array.isArray(stats.recentBlocked)).toBe(true);
    expect(Array.isArray(stats.blocksPerHour)).toBe(true);
  });
});

describe('contract: GET /api/guardrails/log -> BlockedRequest[]', () => {
  it('maps every audit-row field the dashboard renders', async () => {
    const fixture = loadFixture('guardrails-log') as Array<Record<string, unknown>>;
    mockFetchJson(fixture);

    const rows = await fetchGuardrailsLog(1);
    expect(rows.length).toBe(fixture.length);

    const row = rows[0];
    const wire = fixture[0];
    // requestPreview is sourced from the API `requestText` field; a rename would
    // leave it empty, which was one of the shipped regressions.
    expect(row.requestPreview, 'guardrails/log.requestText -> requestPreview').toBe(wire.requestText);
    expect(row.actionTaken, 'guardrails/log.action -> actionTaken').toBe(wire.action);
    expect(row.reason).toBe(wire.reason);
    expect(row.category).toBe(wire.category);
    expect(typeof row.severity, 'guardrails/log.severity').toBe('number');
    expect(row.decision).toBe(wire.decision);
    expect(row.stage).toBe(wire.stage);
    expect(typeof row.threshold, 'guardrails/log.threshold').toBe('number');
    expect(row.subject).toBe(wire.subject);
  });
});

describe('contract: GET /api/cards -> AdaptiveCard[]', () => {
  it('maps integer enums and vote/comment shapes the panel depends on', async () => {
    const fixture = loadFixture('cards') as Array<Record<string, unknown>>;
    mockFetchJson(fixture);

    const cards = await fetchActiveCards();
    const card = cards[0];
    const wire = fixture[0];

    // Enums cross the wire as integers.
    expect(typeof wire.type, 'cards[].type must be an integer enum').toBe('number');
    expect(typeof wire.lifecycle, 'cards[].lifecycle must be an integer enum').toBe('number');
    expect(card.type).toBe(CARD_TYPES[wire.type as number]);
    // Server sends `lifecycle`; the view model exposes `state`. A rename back to a
    // wire `state` field would silently fall back to 'active'.
    expect(card.state).toBe(CARD_LIFECYCLES[wire.lifecycle as number]);

    const wireVote = (wire.votes as Array<Record<string, unknown>>)[0];
    const vote = card.votes?.[0];
    expect(vote, 'cards[].votes must be present').toBeDefined();
    // Server sends { vote, timestamp }; the panel reads { choice, votedAt }.
    expect(vote?.votedAt, 'cards[].votes[].timestamp -> votedAt').toBe(wireVote.timestamp);
    expect(['approve', 'reject', 'abstain']).toContain(vote?.choice);

    const wireComment = (wire.comments as Array<Record<string, unknown>>)[0];
    const comment = card.comments?.[0];
    expect(comment?.text, 'cards[].comments[].text').toBe(wireComment.text);
    expect(card.createdBy).toBe(wire.createdBy);
    expect(card.createdAt).toBe(wire.createdAt);
  });
});

describe('contract: POST /api/scorecard -> PortfolioScorecard', () => {
  it('binds every brand dimension and duration the scorecard reads', async () => {
    const fixture = loadFixture('portfolio-scorecard') as {
      brands: Array<{ brand: string; overallScore: number; durationMs: number }>;
      totalDurationMs: number;
    };
    mockFetchJson(fixture);

    const { brands, durationMs } = await fetchScorecard(['Contoso']);
    const brand = brands[0];
    const wireBrand = fixture.brands[0];

    expect(brand.brandName).toBe(wireBrand.brand);
    // overallScore is reported 0-10; the card health score is 0-100.
    expect(brand.healthScore).toBe(Math.round(wireBrand.overallScore * 10));
    // All five dimensions must bind via agentKey; a rename empties this map and the
    // card shows every dimension at zero.
    expect(Object.keys(brand.dimensionDetails ?? {}).sort()).toEqual([...SCORECARD_DIMENSION_KEYS].sort());
    expect(brand.durationMs, 'scorecard brand.durationMs').toBe(wireBrand.durationMs);
    expect(durationMs, 'scorecard totalDurationMs -> durationMs').toBe(fixture.totalDurationMs);
  });
});
