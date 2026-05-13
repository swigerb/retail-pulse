import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { PromoEvaluation } from '../types';

const mockEvaluation: PromoEvaluation = {
  recommendation: 'recommended',
  roi: 3.2,
  roiLower: 2.1,
  roiUpper: 4.5,
  reasoning: 'Strong historical performance for this brand-region pair.',
  timingAssessment: 'Good timing — no major conflicts.',
  conflicts: [],
  seasonalityFit: 'Peak season',
  risks: [{ type: 'Competition', detail: 'Rival promo active nearby', severity: 'medium' }],
  similarCampaigns: 14,
  breakEvenDays: 21,
  historicalAvgRoi: 2.8,
};

const mockEvaluatePromo = vi.fn().mockResolvedValue(mockEvaluation);
const mockFetchExistingCampaigns = vi.fn().mockResolvedValue([]);
const mockSubmitForApproval = vi.fn().mockResolvedValue(undefined);

vi.mock('../services/promoApi', () => ({
  evaluatePromo: (...args: unknown[]) => mockEvaluatePromo(...args),
  fetchExistingCampaigns: (...args: unknown[]) => mockFetchExistingCampaigns(...args),
  submitForApproval: (...args: unknown[]) => mockSubmitForApproval(...args),
}));

// Must import AFTER vi.mock
import PromoTaskModule from '../components/promo/PromoTaskModule';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('PromoTaskModule', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the campaign planner form', () => {
    render(wrap(<PromoTaskModule />));
    expect(screen.getByTestId('promo-task-module')).toBeInTheDocument();
    expect(screen.getByText('🎯 Campaign Planner')).toBeInTheDocument();
    expect(screen.getByTestId('brand-select')).toBeInTheDocument();
    expect(screen.getByTestId('region-select')).toBeInTheDocument();
    expect(screen.getByTestId('promo-type-selector')).toBeInTheDocument();
  });

  it('renders all promo type cards', () => {
    render(wrap(<PromoTaskModule />));
    expect(screen.getByTestId('promo-type-discount')).toBeInTheDocument();
    expect(screen.getByTestId('promo-type-bogo')).toBeInTheDocument();
    expect(screen.getByTestId('promo-type-display')).toBeInTheDocument();
    expect(screen.getByTestId('promo-type-digital')).toBeInTheDocument();
    expect(screen.getByTestId('promo-type-bundle')).toBeInTheDocument();
  });

  // Helper to fill the entire form
  async function fillForm() {
    fireEvent.change(screen.getByTestId('brand-select'), { target: { value: 'Apex Grill' } });
    fireEvent.change(screen.getByTestId('region-select'), { target: { value: 'Northeast' } });
    fireEvent.click(screen.getByTestId('promo-type-discount'));

    // Fluent UI Input renders native <input> inside the wrapper
    const inputs = document.querySelectorAll<HTMLInputElement>('input');
    const budgetInput = Array.from(inputs).find(i => i.type === 'number');
    const dateInputs = Array.from(inputs).filter(i => i.type === 'date');

    if (budgetInput) {
      await userEvent.clear(budgetInput);
      await userEvent.type(budgetInput, '25000');
    }
    if (dateInputs[0]) {
      fireEvent.change(dateInputs[0], { target: { value: '2026-06-01' } });
    }
    if (dateInputs[1]) {
      fireEvent.change(dateInputs[1], { target: { value: '2026-06-30' } });
    }
  }

  it('evaluate button is disabled when form is incomplete', () => {
    render(wrap(<PromoTaskModule />));
    const button = screen.getByTestId('evaluate-button');
    expect(button).toBeDisabled();
  });

  it('submits form and shows evaluation result', async () => {
    render(wrap(<PromoTaskModule />));
    await fillForm();

    await waitFor(() => {
      expect(screen.getByTestId('evaluate-button')).not.toBeDisabled();
    });
    fireEvent.click(screen.getByTestId('evaluate-button'));

    await waitFor(() => {
      expect(mockEvaluatePromo).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(screen.getByTestId('promo-recommendation')).toBeInTheDocument();
    });
  });

  it('shows loading state during evaluation', async () => {
    mockEvaluatePromo.mockImplementation(
      () => new Promise(resolve => setTimeout(() => resolve(mockEvaluation), 500))
    );
    render(wrap(<PromoTaskModule />));
    await fillForm();

    await waitFor(() => {
      expect(screen.getByTestId('evaluate-button')).not.toBeDisabled();
    });
    fireEvent.click(screen.getByTestId('evaluate-button'));

    await waitFor(() => {
      expect(screen.getByTestId('evaluation-loading')).toBeInTheDocument();
    });
  });

  it('shows error when evaluation fails', async () => {
    mockEvaluatePromo.mockRejectedValue(new Error('API error 500: Server error'));
    render(wrap(<PromoTaskModule />));
    await fillForm();

    await waitFor(() => {
      expect(screen.getByTestId('evaluate-button')).not.toBeDisabled();
    });
    fireEvent.click(screen.getByTestId('evaluate-button'));

    await waitFor(() => {
      expect(screen.getByTestId('evaluation-error')).toBeInTheDocument();
    });
  });

  it('renders calendar component', () => {
    render(wrap(<PromoTaskModule />));
    expect(screen.getByTestId('promo-calendar')).toBeInTheDocument();
  });
});
