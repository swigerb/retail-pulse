import type { IntentCategory } from '../types';

// Forecast chart theme colors
export const FORECAST_COLORS = {
  demandAgent: '#6366f1',    // indigo — routing indicator + chart theme
  forecastLine: '#8b5cf6',   // violet — predicted line
  confidenceBand: '#8b5cf620', // violet at 12% opacity
  actualLine: '#3b82f6',     // blue — historical actual line
  todayLine: '#94a3b8',      // slate — vertical "today" divider
} as const;

// Seasonal annotation colors
export const SEASONAL_COLORS: Record<string, string> = {
  holiday: '#ef444440',
  summer: '#f9731640',
  'back-to-school': '#22c55e40',
  default: '#6366f140',
} as const;

export const AGENT_COLORS: Record<IntentCategory, string> = {
  demand: '#6366f1',
  promotion: '#22c55e',
  supply: '#f97316',
  competitive: '#ef4444',
  sentiment: '#a855f7',
  general: '#6b7280',
};

export const AGENT_EMOJIS: Record<IntentCategory, string> = {
  demand: '📈',
  promotion: '🎯',
  supply: '📦',
  competitive: '⚔️',
  sentiment: '💬',
  general: '🤖',
};

export const AGENT_LABELS: Record<IntentCategory, string> = {
  demand: 'Demand',
  promotion: 'Promotion',
  supply: 'Supply',
  competitive: 'Competitive',
  sentiment: 'Sentiment',
  general: 'General',
};

// Promotion planning chart colors
export const PROMO_COLORS = {
  recommended: '#22c55e',
  cautious: '#eab308',
  notRecommended: '#ef4444',
  roi: '#22c55e',
  roiBelow: '#ef4444',
  breakEven: '#94a3b8',
  confidence: '#3b82f6',
  historical: '#6b7280',
  proposedBar: '#8b5cf6',
  calendarActive: '#3b82f6',
  calendarCompleted: '#6b7280',
  calendarPlanned: '#a855f7',
  calendarProposed: '#22c55e',
  calendarConflict: '#ef4444',
} as const;

export const PROMO_TYPE_CONFIG: Record<string, { emoji: string; description: string; hint: string }> = {
  Discount: { emoji: '💰', description: 'Price reduction on selected items', hint: 'Best for: price-sensitive categories' },
  BOGO: { emoji: '🎁', description: 'Buy one get one free/discounted', hint: 'Best for: impulse & trial categories' },
  Display: { emoji: '🏪', description: 'In-store display & placement', hint: 'Best for: seasonal & new products' },
  Digital: { emoji: '📱', description: 'Online & mobile campaigns', hint: 'Best for: younger demographics' },
  Bundle: { emoji: '📦', description: 'Multi-product package deals', hint: 'Best for: complementary products' },
} as const;

// Competitive Intelligence colors (Sprint 2.2)
export const COMPETITIVE_COLORS = {
  ourBrand: '#3b82f6',
  competitor: '#6b7280',
  competitorMuted: 'rgba(107,114,128,0.4)',
  threatHigh: '#ef4444',
  threatMedium: '#f59e0b',
  threatLow: '#22c55e',
  priceUp: '#ef4444',
  priceDown: '#22c55e',
  priceNeutral: '#94a3b8',
  match: '#f59e0b',
  differentiate: '#3b82f6',
  ignore: '#6b7280',
  preempt: '#a855f7',
  shareArea: '#3b82f620',
  gridLine: 'rgba(255,255,255,0.06)',
} as const;

// Council / Portfolio Health colors (Sprint 2.4)
export const COUNCIL_COLORS = {
  green: '#0f7b0f',
  yellow: '#d4a017',
  red: '#d32f2f',
  greenBg: 'rgba(15,123,15,0.15)',
  yellowBg: 'rgba(212,160,23,0.15)',
  redBg: 'rgba(211,47,47,0.15)',
  greenGlow: '0 0 20px rgba(15,123,15,0.4)',
  yellowGlow: '0 0 20px rgba(212,160,23,0.4)',
  redGlow: '0 0 20px rgba(211,47,47,0.4)',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
  verdictBg: 'rgba(255,255,255,0.04)',
  disagreementBg: 'rgba(212,160,23,0.08)',
  disagreementBorder: 'rgba(212,160,23,0.25)',
  demandIcon: '#6366f1',
  supplyIcon: '#f97316',
  competitiveIcon: '#ef4444',
} as const;

export const COUNCIL_DOMAIN_CONFIG: Record<string, { emoji: string; label: string; color: string }> = {
  demand: { emoji: '📊', label: 'Demand Analyst', color: COUNCIL_COLORS.demandIcon },
  supply: { emoji: '🏭', label: 'Supply Analyst', color: COUNCIL_COLORS.supplyIcon },
  competitive: { emoji: '🎯', label: 'Competitive Analyst', color: COUNCIL_COLORS.competitiveIcon },
} as const;

// Knowledge Base colors (Sprint 2.3)
export const KB_COLORS = {
  primary: '#06b6d4',
  accent: '#0ea5e9',
  success: '#22c55e',
  dropZone: 'rgba(6,182,212,0.1)',
  dropZoneBorder: '#06b6d4',
  citationPill: 'rgba(6,182,212,0.15)',
  citationText: '#67e8f9',
  relevanceHigh: '#22c55e',
  relevanceMedium: '#f59e0b',
  relevanceLow: '#94a3b8',
} as const;

// Collaborative Adaptive Cards colors (Sprint 3.3)
export const CARD_COLORS = {
  active: '#3b82f6',
  voting: '#f59e0b',
  decided: '#22c55e',
  archived: '#6b7280',
  activeBg: 'rgba(59,130,246,0.15)',
  votingBg: 'rgba(245,158,11,0.15)',
  decidedBg: 'rgba(34,197,94,0.15)',
  archivedBg: 'rgba(107,114,128,0.15)',
  approve: '#22c55e',
  reject: '#ef4444',
  abstain: '#94a3b8',
  escalation: '#f59e0b',
  escalationBg: 'rgba(245,158,11,0.12)',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
  commentBg: 'rgba(255,255,255,0.04)',
} as const;

export const CARD_TYPE_CONFIG: Record<string, { emoji: string; label: string }> = {
  voting: { emoji: '🗳️', label: 'Voting' },
  drilldown: { emoji: '🔍', label: 'Drill Down' },
  dashboard: { emoji: '📊', label: 'Dashboard' },
  briefing: { emoji: '📋', label: 'Briefing' },
} as const;

export const CARD_LIFECYCLE_CONFIG: Record<string, { label: string; color: string; bg: string }> = {
  active: { label: 'Active', color: CARD_COLORS.active, bg: CARD_COLORS.activeBg },
  voting: { label: 'Voting', color: CARD_COLORS.voting, bg: CARD_COLORS.votingBg },
  decided: { label: 'Decided', color: CARD_COLORS.decided, bg: CARD_COLORS.decidedBg },
  archived: { label: 'Archived', color: CARD_COLORS.archived, bg: CARD_COLORS.archivedBg },
} as const;

// Observability colors (Sprint 3.4)
export const OBSERVABILITY_COLORS = {
  primary: '#06b6d4',
  cost: '#f59e0b',
  tokens: '#8b5cf6',
  requests: '#3b82f6',
  avgCost: '#22c55e',
  trendLine: '#06b6d4',
  barFill: '#3b82f6',
  gridLine: 'rgba(255,255,255,0.06)',
  tabActive: '#06b6d4',
  tabInactive: 'rgba(255,255,255,0.4)',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
} as const;

// Store Operations colors (Sprint 4.1)
export const STORE_COLORS = {
  green: '#22c55e',
  yellow: '#eab308',
  red: '#ef4444',
  greenBg: 'rgba(34,197,94,0.15)',
  yellowBg: 'rgba(234,179,8,0.15)',
  redBg: 'rgba(239,68,68,0.15)',
  eyeLevel: 'rgba(99,102,241,0.18)',
  eyeLevelBorder: '#6366f1',
  shelfBg: 'rgba(255,255,255,0.04)',
  shelfBorder: 'rgba(255,255,255,0.1)',
  uplift: '#22c55e',
  stockoutUrgent: '#ef4444',
  stockoutWarning: '#f59e0b',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
  gridLine: 'rgba(255,255,255,0.06)',
  heatmapHover: 'rgba(255,255,255,0.08)',
} as const;

// Margin & Escalation colors (Sprint 4.2)
export const MARGIN_COLORS = {
  revenue: '#3b82f6',
  cost: '#ef4444',
  profit: '#22c55e',
  neutral: '#94a3b8',
  positiveImpact: '#22c55e',
  negativeImpact: '#ef4444',
  improving: '#22c55e',
  worsening: '#ef4444',
  stable: '#94a3b8',
  escalationL1: '#3b82f6',
  escalationL2: '#f59e0b',
  escalationL3: '#ef4444',
  escalationCurrent: '#8b5cf6',
  escalationLine: 'rgba(255,255,255,0.15)',
  escalationBg: 'rgba(255,255,255,0.03)',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
  waterfallPositive: '#22c55e',
  waterfallNegative: '#ef4444',
  waterfallSubtotal: '#3b82f6',
} as const;

// Portfolio Scorecard colors (Sprint 4.3)
export const SCORECARD_COLORS = {
  green: '#22c55e',
  amber: '#f59e0b',
  red: '#ef4444',
  greenBg: 'rgba(34,197,94,0.12)',
  amberBg: 'rgba(245,158,11,0.12)',
  redBg: 'rgba(239,68,68,0.12)',
  greenGlow: '0 0 16px rgba(34,197,94,0.3)',
  amberGlow: '0 0 16px rgba(245,158,11,0.3)',
  redGlow: '0 0 16px rgba(239,68,68,0.3)',
  ring: '#6366f1',
  ringTrack: 'rgba(255,255,255,0.08)',
  cardBg: 'rgba(255,255,255,0.03)',
  cardBorder: 'rgba(255,255,255,0.08)',
  dimensionDemand: '#6366f1',
  dimensionMargin: '#22c55e',
  dimensionCompetitive: '#ef4444',
  dimensionSupply: '#f97316',
  dimensionStore: '#06b6d4',
  skeletonBg: 'rgba(255,255,255,0.06)',
  skeletonShimmer: 'rgba(255,255,255,0.1)',
  whyButton: '#8b5cf6',
  whyButtonBg: 'rgba(139,92,246,0.12)',
  explainPanel: 'rgba(30,30,40,0.98)',
  explainBorder: 'rgba(255,255,255,0.1)',
  stepBadge: 'rgba(99,102,241,0.2)',
  stepBadgeText: '#a5b4fc',
  confidenceHigh: '#22c55e',
  confidenceMedium: '#f59e0b',
  confidenceLow: '#ef4444',
} as const;
