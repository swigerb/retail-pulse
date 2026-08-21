import type { PromptCategory } from '../constants/prompts';

/**
 * Frontend projections of the active content pack surfaced by
 * `/api/pack` and `/api/pack/starting-tasks`. Field names mirror the
 * camelCase JSON produced by Minimal API endpoints in
 * `RetailPulse.Api.Endpoints.PackEndpoints`.
 *
 * A pack switch is a backend configuration change (Packs:Active) plus a
 * restart; the frontend never chooses a pack. It just projects whichever
 * pack the server booted with so the header, prompt library, and theme
 * stay coherent with the tenant and knowledge grounding the server is
 * actually using.
 */

export interface PackBrand {
  readonly name: string;
  readonly category: string;
  readonly variants: readonly string[];
  readonly priceSegment: string;
}

export interface PackTheme {
  readonly primaryColor: string;
  readonly accentColor: string;
  readonly logoPath: string;
  readonly fontFamily: string;
}

export interface PackDistribution {
  readonly model: string;
  readonly distributorTypes: readonly string[];
}

export interface PackTenant {
  readonly company: string;
  readonly industry: string;
  readonly description: string;
  readonly brands: readonly PackBrand[];
  readonly regions: readonly string[];
  readonly channels: readonly string[];
  readonly theme: PackTheme;
  readonly distribution: PackDistribution;
}

export interface PackInfo {
  readonly key: string;
  readonly displayName: string;
  readonly description: string;
  readonly version: string;
  readonly segment: string;
  readonly attribution: string;
  readonly tenant: PackTenant;
}

export interface PackStartingTasks {
  readonly packKey: string;
  readonly categories: readonly PromptCategory[];
}
