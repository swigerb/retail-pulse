import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { CouncilAgentVote, CouncilVerdict, CouncilConveneResponse } from '../types';

// --- Mock data ---

const mockVoteGreen: CouncilAgentVote = {
  agentId: 'demand-agent',
  agentName: 'Demand Analyst',
  domain: 'demand',
  rating: 'green',
  confidence: 92,
  reasoning: 'Strong demand signals across all regions with consistent growth trends.',
  keyMetrics: ['YoY Growth +8.2%', 'Forecast Accuracy 94%', 'Regional Coverage 100%'],
  responseTimeMs: 1240,
};

const mockVoteYellow: CouncilAgentVote = {
  agentId: 'supply-agent',
  agentName: 'Supply Analyst',
  domain: 'supply',
  rating: 'yellow',
  confidence: 78,
  reasoning: 'Supply chain shows minor disruption risk in Southeast distribution.',
  keyMetrics: ['Fill Rate 96%', 'Lead Time +2 days', 'Inventory Weeks 3.2'],
  responseTimeMs: 980,
};

const mockVoteRed: CouncilAgentVote = {
  agentId: 'competitive-agent',
  agentName: 'Competitive Analyst',
  domain: 'competitive',
  rating: 'red',
  confidence: 85,
  reasoning: 'Aggressive competitor pricing detected, market share at risk.',
  keyMetrics: ['Share Loss -2.1%', 'Price Gap -12%', 'New Entrants: 2'],
  responseTimeMs: 1560,
};

const mockVoteTimedOut: CouncilAgentVote = {
  agentId: 'supply-agent',
  agentName: 'Supply Analyst',
  domain: 'supply',
  rating: 'yellow',
  confidence: 0,
  reasoning: '',
  keyMetrics: [],
  responseTimeMs: 30000,
  timedOut: true,
};

const mockVerdictUnanimous: CouncilVerdict = {
  overallRating: 'green',
  unanimous: true,
  synthesisText: 'Brand shows strong performance across all dimensions. All specialist agents agree on a healthy outlook.',
  disagreements: [],
  actionItems: [
    { priority: 1, text: 'Maintain current pricing strategy' },
    { priority: 2, text: 'Increase marketing spend in Southeast to capitalize on demand growth' },
  ],
  totalConveneTimeMs: 3780,
};

const mockVerdictSplit: CouncilVerdict = {
  overallRating: 'yellow',
  unanimous: false,
  synthesisText: 'Mixed signals: strong demand fundamentals but competitive pressure warrants attention.',
  disagreements: [
    {
      topic: 'Pricing Strategy Response',
      agents: [
        { agentName: 'Demand Analyst', position: 'Current pricing is optimal for demand capture' },
        { agentName: 'Competitive Analyst', position: 'Must adjust pricing to counter competitor aggression' },
      ],
      resolution: 'Selective price matching in high-competition regions while maintaining premium in strongholds.',
      dominantAgent: 'Competitive Analyst',
      dominantReason: 'Immediate market share threat requires urgent response',
    },
  ],
  actionItems: [
    { priority: 1, text: 'Implement selective price matching in Southeast' },
    { priority: 2, text: 'Monitor competitor expansion closely' },
    { priority: 3, text: 'Strengthen brand differentiation messaging' },
  ],
  totalConveneTimeMs: 4200,
};

const mockConveneResponse: CouncilConveneResponse = {
  sessionId: 'session-1',
  brand: 'Apex Grill',
  votes: [mockVoteGreen, mockVoteYellow, mockVoteRed],
  verdict: mockVerdictSplit,
};

// --- Mocks ---

vi.mock('../services/councilApi', () => ({
  conveneCouncil: vi.fn(),
  fetchCouncilHistory: vi.fn(),
}));

import { conveneCouncil, fetchCouncilHistory } from '../services/councilApi';
import VoteCard from '../components/council/VoteCard';
import CouncilVerdictView from '../components/council/CouncilVerdict';
import CouncilPanel from '../components/council/CouncilPanel';

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <FluentProvider theme={teamsDarkTheme}>{children}</FluentProvider>
);

// --- VoteCard Tests ---

describe('VoteCard', () => {
  it('renders agent name and domain', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    const nameElements = screen.getAllByText('Demand Analyst');
    expect(nameElements.length).toBeGreaterThanOrEqual(1);
  });

  it('renders green rating label', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByTestId('vote-rating')).toHaveTextContent('Healthy');
  });

  it('renders yellow rating label', () => {
    render(<VoteCard vote={mockVoteYellow} index={0} />, { wrapper });
    expect(screen.getByTestId('vote-rating')).toHaveTextContent('Caution');
  });

  it('renders red rating label', () => {
    render(<VoteCard vote={mockVoteRed} index={0} />, { wrapper });
    expect(screen.getByTestId('vote-rating')).toHaveTextContent('At Risk');
  });

  it('renders reasoning text', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByText(mockVoteGreen.reasoning)).toBeInTheDocument();
  });

  it('renders key metrics as pills', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByTestId('vote-metrics')).toBeInTheDocument();
    expect(screen.getByText('YoY Growth +8.2%')).toBeInTheDocument();
    expect(screen.getByText('Forecast Accuracy 94%')).toBeInTheDocument();
  });

  it('renders confidence bar and percentage', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByTestId('confidence-bar')).toBeInTheDocument();
    expect(screen.getByText('92%')).toBeInTheDocument();
  });

  it('renders response time badge', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByText('⚡ 1240ms')).toBeInTheDocument();
  });

  it('renders timed-out state correctly', () => {
    render(<VoteCard vote={mockVoteTimedOut} index={0} />, { wrapper });
    expect(screen.getByTestId('vote-card-timedout')).toBeInTheDocument();
    expect(screen.getByText(/Timed out/)).toBeInTheDocument();
  });

  it('has correct aria-label for accessibility', () => {
    render(<VoteCard vote={mockVoteGreen} index={0} />, { wrapper });
    expect(screen.getByRole('article')).toHaveAttribute('aria-label', 'Demand Analyst votes Healthy');
  });
});

// --- CouncilVerdict Tests ---

describe('CouncilVerdict', () => {
  it('renders unanimous verdict with green indicator', () => {
    render(<CouncilVerdictView verdict={mockVerdictUnanimous} />, { wrapper });
    expect(screen.getByTestId('council-verdict')).toBeInTheDocument();
    expect(screen.getByTestId('verdict-rating')).toBeInTheDocument();
    expect(screen.getByTestId('unanimous-badge')).toHaveTextContent('✓ Unanimous');
  });

  it('renders split decision badge', () => {
    render(<CouncilVerdictView verdict={mockVerdictSplit} />, { wrapper });
    expect(screen.getByTestId('unanimous-badge')).toHaveTextContent('⚠️ Split Decision');
  });

  it('renders synthesis text', () => {
    render(<CouncilVerdictView verdict={mockVerdictUnanimous} />, { wrapper });
    expect(screen.getByTestId('synthesis-text')).toHaveTextContent(mockVerdictUnanimous.synthesisText);
  });

  it('renders action items with priority badges', () => {
    render(<CouncilVerdictView verdict={mockVerdictSplit} />, { wrapper });
    expect(screen.getByTestId('action-items')).toBeInTheDocument();
    expect(screen.getByText('Implement selective price matching in Southeast')).toBeInTheDocument();
  });

  it('renders disagreements when present', () => {
    render(<CouncilVerdictView verdict={mockVerdictSplit} />, { wrapper });
    expect(screen.getByTestId('disagreement-highlight')).toBeInTheDocument();
  });

  it('does not render disagreements section when unanimous', () => {
    render(<CouncilVerdictView verdict={mockVerdictUnanimous} />, { wrapper });
    expect(screen.queryByTestId('disagreement-highlight')).not.toBeInTheDocument();
  });

  it('displays convene time', () => {
    render(<CouncilVerdictView verdict={mockVerdictSplit} />, { wrapper });
    expect(screen.getByTestId('convene-time')).toHaveTextContent('4.2s');
  });
});

// --- CouncilPanel Tests ---

describe('CouncilPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders brand selector and convene button', () => {
    render(<CouncilPanel />, { wrapper });
    expect(screen.getByTestId('brand-selector')).toBeInTheDocument();
    expect(screen.getByTestId('region-selector')).toBeInTheDocument();
    expect(screen.getByTestId('convene-button')).toBeInTheDocument();
  });

  it('shows empty state initially', () => {
    render(<CouncilPanel />, { wrapper });
    expect(screen.getByText('Portfolio Health Council')).toBeInTheDocument();
    expect(screen.getByText(/Select a brand/)).toBeInTheDocument();
  });

  it('calls conveneCouncil API when button clicked', async () => {
    vi.mocked(conveneCouncil).mockResolvedValue(mockConveneResponse);
    render(<CouncilPanel />, { wrapper });

    fireEvent.click(screen.getByTestId('convene-button'));

    await waitFor(() => {
      expect(conveneCouncil).toHaveBeenCalledWith('Apex Grill', undefined);
    });
  });

  it('passes selected region to API', async () => {
    vi.mocked(conveneCouncil).mockResolvedValue(mockConveneResponse);
    render(<CouncilPanel />, { wrapper });

    fireEvent.change(screen.getByTestId('region-selector'), { target: { value: 'Northeast' } });
    fireEvent.click(screen.getByTestId('convene-button'));

    await waitFor(() => {
      expect(conveneCouncil).toHaveBeenCalledWith('Apex Grill', 'Northeast');
    });
  });

  it('shows votes and verdict after successful convene', async () => {
    vi.mocked(conveneCouncil).mockResolvedValue(mockConveneResponse);
    render(<CouncilPanel />, { wrapper });

    fireEvent.click(screen.getByTestId('convene-button'));

    await waitFor(() => {
      expect(screen.getByTestId('council-verdict')).toBeInTheDocument();
    }, { timeout: 5000 });
  });

  it('shows error message on API failure', async () => {
    vi.mocked(conveneCouncil).mockRejectedValue(new Error('Network error'));
    render(<CouncilPanel />, { wrapper });

    fireEvent.click(screen.getByTestId('convene-button'));

    await waitFor(() => {
      expect(screen.getByTestId('council-error')).toBeInTheDocument();
      expect(screen.getByText(/Network error/)).toBeInTheDocument();
    });
  });

  it('disables controls during convene', async () => {
    vi.mocked(conveneCouncil).mockImplementation(
      () => new Promise(resolve => setTimeout(() => resolve(mockConveneResponse), 2000))
    );
    render(<CouncilPanel />, { wrapper });

    fireEvent.click(screen.getByTestId('convene-button'));

    expect(screen.getByTestId('brand-selector')).toBeDisabled();
    expect(screen.getByTestId('region-selector')).toBeDisabled();
    expect(screen.getByTestId('convene-button')).toBeDisabled();
  });

  it('distinguishes a failed history load from an empty history', async () => {
    vi.mocked(fetchCouncilHistory).mockRejectedValue(new Error('Network error'));
    render(<CouncilPanel />, { wrapper });

    fireEvent.click(screen.getByText('Load History'));

    await waitFor(() => {
      expect(screen.getByTestId('history-error')).toHaveTextContent('Unable to load previous council sessions');
      expect(screen.queryByTestId('history-empty')).not.toBeInTheDocument();
    });
  });
});
