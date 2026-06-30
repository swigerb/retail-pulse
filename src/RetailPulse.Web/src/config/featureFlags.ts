const featureFlagDefaults = {
  campaignPlanner: false,
  competitive: false,
  knowledgeBase: false,
  healthCouncil: false,
  security: false,
  cards: false,
  stores: false,
  financials: false,
  portfolio: false,
  observability: true,
} as const;

export type FeatureKey = keyof typeof featureFlagDefaults;

export type FeatureFlags = Record<FeatureKey, boolean>;

export function parseFeatureFlag(value: string | undefined, defaultValue: boolean): boolean {
  const normalized = value?.trim().toLowerCase();
  return normalized === 'true' || normalized === '1' ? true : defaultValue;
}

export const featureFlags: FeatureFlags = {
  campaignPlanner: parseFeatureFlag(import.meta.env.VITE_FEATURE_CAMPAIGN_PLANNER, featureFlagDefaults.campaignPlanner),
  competitive: parseFeatureFlag(import.meta.env.VITE_FEATURE_COMPETITIVE, featureFlagDefaults.competitive),
  knowledgeBase: parseFeatureFlag(import.meta.env.VITE_FEATURE_KNOWLEDGE_BASE, featureFlagDefaults.knowledgeBase),
  healthCouncil: parseFeatureFlag(import.meta.env.VITE_FEATURE_HEALTH_COUNCIL, featureFlagDefaults.healthCouncil),
  security: parseFeatureFlag(import.meta.env.VITE_FEATURE_SECURITY, featureFlagDefaults.security),
  cards: parseFeatureFlag(import.meta.env.VITE_FEATURE_CARDS, featureFlagDefaults.cards),
  stores: parseFeatureFlag(import.meta.env.VITE_FEATURE_STORES, featureFlagDefaults.stores),
  financials: parseFeatureFlag(import.meta.env.VITE_FEATURE_FINANCIALS, featureFlagDefaults.financials),
  portfolio: parseFeatureFlag(import.meta.env.VITE_FEATURE_PORTFOLIO, featureFlagDefaults.portfolio),
  observability: parseFeatureFlag(import.meta.env.VITE_FEATURE_OBSERVABILITY, featureFlagDefaults.observability),
};
