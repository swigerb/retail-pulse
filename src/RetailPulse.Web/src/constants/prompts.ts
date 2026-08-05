/**
 * Single source of truth for the curated retail prompt library.
 *
 * Both the empty New Chat welcome state and the always-available prompt-library
 * popover consume these definitions, so the prompt text and categories are never
 * duplicated. Update prompts here and every surface stays in sync.
 */

export interface PromptCategory {
  id: string;
  label: string;
  emoji: string;
  prompts: string[];
}

export const PROMPT_CATEGORIES: ReadonlyArray<PromptCategory> = [
  {
    id: 'general',
    label: 'General Retail',
    emoji: '📊',
    prompts: [
      'Compare depletion trends across all regions for this quarter',
      'Which brands are growing fastest year-over-year across the portfolio?',
      'Show me field sentiment for our top 3 brands in the Southeast',
    ],
  },
  {
    id: 'grocery',
    label: 'Grocery',
    emoji: '🛒',
    prompts: [
      'How are FreshMart depletions trending in the Northeast this quarter?',
      'Compare Harvest Table vs FreshMart sell-through rates by region',
      'What is the field sentiment for Harvest Table Meal Kits in the Midwest?',
    ],
  },
  {
    id: 'qsr',
    label: 'Quick-Serve Restaurants',
    emoji: '🍔',
    prompts: [
      'How is Apex Grill performing in the Southwest this quarter?',
      'Compare Coastline Tacos vs Apex Grill depletions across all regions',
      'What is the field sentiment for Coastline Tacos in the West Coast?',
    ],
  },
  {
    id: 'home-improvement',
    label: 'Home Improvement',
    emoji: '🏠',
    prompts: [
      'Show me Pinnacle Hardware depletion stats in the Midwest for Q1',
      'How is Summit Outdoor performing in the Southeast vs West Coast?',
      'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?',
    ],
  },
  {
    id: 'office-supply',
    label: 'Office Supply',
    emoji: '📎',
    prompts: [
      'How are ClearDesk depletions trending in the Northeast this quarter?',
      'Compare ClearDesk Technology vs Paper Products sell-through by region',
      'What is the field sentiment for ClearDesk in the Southeast?',
    ],
  },
  {
    id: 'furniture',
    label: 'Furniture',
    emoji: '🛋️',
    prompts: [
      'Show me Urban Living depletion trends across all regions this quarter',
      'Compare Foundry Home vs Urban Living performance in the West Coast',
      'What is the field sentiment for Urban Living in the Pacific Northwest?',
    ],
  },
  {
    id: 'charts',
    label: 'Charts',
    emoji: '📈',
    prompts: [
      'Create a line chart showing Sierra Gold Tequila depletion trends across all regions',
      'Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast',
      'Create a pie chart showing market share breakdown for our grocery brands nationally',
      'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions',
      'Create a donut chart of Apex Grill variant mix in the Southwest',
      'Show a horizontal bar chart ranking all brands by depletion growth rate',
      'Create a table showing depletion stats for all home improvement brands by region',
      'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest',
    ],
  },
];
