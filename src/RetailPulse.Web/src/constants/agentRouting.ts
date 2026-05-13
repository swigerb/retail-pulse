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
