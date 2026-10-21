import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { RegulatedModeBadge } from '../components/RegulatedModeBadge';

const base = { mode: 'regulated-retail', disclaimer: 'Not HIPAA certified; no patient data or clinical advice.' };

describe('RegulatedModeBadge', () => {
  it('shows the regulated label without claiming certification', () => {
    render(<RegulatedModeBadge profile={{ ...base, enabled: true, label: 'Regulated-retail mode (synthetic data only, not HIPAA certified)' }} />);
    const badge = screen.getByTestId('regulated-mode-badge');
    expect(badge.textContent).toMatch(/not HIPAA certified/i);
    expect(badge.getAttribute('title')).toMatch(/no patient data/i);
  });

  it('renders nothing in synthetic demo mode', () => {
    render(<RegulatedModeBadge profile={{ ...base, mode: 'synthetic-demo', enabled: false, label: 'Synthetic demo mode' }} />);
    expect(screen.queryByTestId('regulated-mode-badge')).toBeNull();
  });
});
