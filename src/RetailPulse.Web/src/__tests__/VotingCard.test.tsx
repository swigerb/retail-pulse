import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import VotingCard from '../components/cards/VotingCard';
import type { AdaptiveCard, UserVote } from '../types';

const wrap = (ui: React.ReactNode) => (
  <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>
);

const baseCard: AdaptiveCard = {
  id: 'card-1',
  type: 'voting',
  title: 'Restock Southeast Apex Grill',
  summary: 'Demand analyst recommends a 15% inventory increase for Southeast region.',
  state: 'voting',
  stateChangedAt: new Date().toISOString(),
  createdAt: new Date().toISOString(),
  createdBy: 'demand-agent',
  votes: [],
  comments: [],
};

function makeVotes(approve: number, reject: number, abstain: number): UserVote[] {
  const votes: UserVote[] = [];
  for (let i = 0; i < approve; i++) {
    votes.push({ userId: `u-a${i}`, userName: `Approver ${i}`, choice: 'approve', votedAt: new Date().toISOString() });
  }
  for (let i = 0; i < reject; i++) {
    votes.push({ userId: `u-r${i}`, userName: `Rejector ${i}`, choice: 'reject', votedAt: new Date().toISOString() });
  }
  for (let i = 0; i < abstain; i++) {
    votes.push({ userId: `u-s${i}`, userName: `Abstainer ${i}`, choice: 'abstain', votedAt: new Date().toISOString() });
  }
  return votes;
}

describe('VotingCard', () => {
  it('renders title and summary', () => {
    render(wrap(
      <VotingCard card={baseCard} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByText(baseCard.title)).toBeInTheDocument();
    expect(screen.getByText(baseCard.summary)).toBeInTheDocument();
  });

  it('renders three vote buttons when user has not voted', () => {
    render(wrap(
      <VotingCard card={baseCard} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByTestId('vote-btn-approve')).toBeInTheDocument();
    expect(screen.getByTestId('vote-btn-reject')).toBeInTheDocument();
    expect(screen.getByTestId('vote-btn-abstain')).toBeInTheDocument();
  });

  it('calls onVote when approve button is clicked', () => {
    const onVote = vi.fn();
    render(wrap(
      <VotingCard card={baseCard} currentUserId="me" onVote={onVote} />
    ));
    fireEvent.click(screen.getByTestId('vote-btn-approve'));
    expect(onVote).toHaveBeenCalledWith('approve');
  });

  it('disables buttons when user has already voted', () => {
    const cardWithMyVote: AdaptiveCard = {
      ...baseCard,
      votes: [{ userId: 'me', userName: 'Me', choice: 'approve', votedAt: new Date().toISOString() }],
    };
    render(wrap(
      <VotingCard card={cardWithMyVote} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByText(/You voted/i)).toBeInTheDocument();
  });

  it('shows vote tally bar when votes exist', () => {
    const cardWithVotes: AdaptiveCard = {
      ...baseCard,
      votes: makeVotes(3, 1, 0),
    };
    render(wrap(
      <VotingCard card={cardWithVotes} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByTestId('vote-tally')).toBeInTheDocument();
  });

  it('shows split vote warning when approve ≈ reject', () => {
    const cardSplit: AdaptiveCard = {
      ...baseCard,
      votes: makeVotes(3, 3, 0),
    };
    render(wrap(
      <VotingCard card={cardSplit} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByTestId('split-vote-warning')).toBeInTheDocument();
  });

  it('does not show split vote warning when one side clearly wins', () => {
    const cardClear: AdaptiveCard = {
      ...baseCard,
      votes: makeVotes(5, 1, 0),
    };
    render(wrap(
      <VotingCard card={cardClear} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.queryByTestId('split-vote-warning')).not.toBeInTheDocument();
  });

  it('shows escalation banner when card is escalated', () => {
    const escalatedCard: AdaptiveCard = {
      ...baseCard,
      escalated: true,
      escalationReason: 'Split vote detected — escalating to council',
    };
    render(wrap(
      <VotingCard card={escalatedCard} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByTestId('escalation-banner')).toBeInTheDocument();
  });

  it('renders voter pills showing who voted', () => {
    const cardWithVotes: AdaptiveCard = {
      ...baseCard,
      votes: makeVotes(2, 1, 0),
    };
    render(wrap(
      <VotingCard card={cardWithVotes} currentUserId="me" onVote={vi.fn()} />
    ));
    expect(screen.getByText('Approver 0')).toBeInTheDocument();
    expect(screen.getByText('Rejector 0')).toBeInTheDocument();
  });
});
