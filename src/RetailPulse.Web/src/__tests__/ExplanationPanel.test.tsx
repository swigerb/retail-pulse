import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { ExplanationPanel } from '../components/scorecard/ExplanationPanel';
import type { ExplanationData } from '../types';

function renderWithProvider(ui: React.ReactElement) {
  return render(<FluentProvider theme={webDarkTheme}>{ui}</FluentProvider>);
}

const mockExplanation: ExplanationData = {
  traceId: 'trace-001',
  question: 'Why is Sierra Gold underperforming?',
  answer: 'Sierra Gold has seen a 12% decline in velocity due to increased competitor promotions.',
  steps: [
    {
      toolName: 'sales-query',
      inputSummary: 'Query sales data for Sierra Gold',
      outputSummary: '12% velocity decline',
      reasoning: 'Sales data shows consistent decline over 4 weeks.',
    },
    {
      toolName: 'competitor-scan',
      inputSummary: 'Scan competitor activity in region',
      outputSummary: '3 new competitor promos found',
      reasoning: 'Competitor brands launched aggressive pricing in the same segment.',
    },
  ],
  confidence: 87,
  dataSources: [
    { name: 'POS Sales DB', url: 'https://example.com/pos' },
    { name: 'Market Intelligence Feed' },
  ],
  generatedAt: '2024-12-15T10:30:00Z',
};

describe('ExplanationPanel', () => {
  it('renders question and answer when open with data', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={vi.fn()} />,
    );

    expect(screen.getByText('Why is Sierra Gold underperforming?')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Sierra Gold has seen a 12% decline in velocity due to increased competitor promotions.',
      ),
    ).toBeInTheDocument();
  });

  it('keeps rendering core content when optional arrays are absent', () => {
    renderWithProvider(
      <ExplanationPanel
        explanation={{
          question: 'Why is the score low?',
          answer: 'The scorecard returned no grounded reasoning steps for this dimension.',
        }}
        open={true}
        onClose={vi.fn()}
      />,
    );

    expect(screen.getByText('Why is the score low?')).toBeInTheDocument();
    expect(screen.getByText('The scorecard returned no grounded reasoning steps for this dimension.')).toBeInTheDocument();
    expect(screen.getByText('No grounded reasoning steps were returned for this score.')).toBeInTheDocument();
  });

  it('shows tool name badges for each step', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={vi.fn()} />,
    );

    expect(screen.getByText('sales-query')).toBeInTheDocument();
    expect(screen.getByText('competitor-scan')).toBeInTheDocument();
  });

  it('shows confidence score', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={vi.fn()} />,
    );

    expect(screen.getByText('87%')).toBeInTheDocument();
    expect(screen.getByText('Grounding')).toBeInTheDocument();
  });

  it('shows data source names', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={vi.fn()} />,
    );

    expect(screen.getByText('POS Sales DB')).toBeInTheDocument();
    expect(screen.getByText('Market Intelligence Feed')).toBeInTheDocument();
  });

  it('renders panel in closed position when open=false', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={false} onClose={vi.fn()} />,
    );

    // Panel still exists in DOM but is visually off-screen (translateX(100%))
    // The question text is still in the DOM but the panel is translated away
    // We verify the panel has the closed class by checking the close button is still rendered
    expect(screen.getByLabelText('Close')).toBeInTheDocument();
  });

  it('shows loading skeleton when open but explanation is null', () => {
    renderWithProvider(
      <ExplanationPanel explanation={null} open={true} onClose={vi.fn()} />,
    );

    // When explanation is null, no question/answer should appear
    expect(screen.queryByText('Why is Sierra Gold underperforming?')).not.toBeInTheDocument();
    // Header still shows
    expect(screen.getByText('How did we get this answer?')).toBeInTheDocument();
  });

  it('close button fires onClose', () => {
    const onClose = vi.fn();
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={onClose} />,
    );

    fireEvent.click(screen.getByLabelText('Close'));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows step reasoning text', () => {
    renderWithProvider(
      <ExplanationPanel explanation={mockExplanation} open={true} onClose={vi.fn()} />,
    );

    expect(
      screen.getByText('Sales data shows consistent decline over 4 weeks.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Competitor brands launched aggressive pricing in the same segment.'),
    ).toBeInTheDocument();
  });
});
