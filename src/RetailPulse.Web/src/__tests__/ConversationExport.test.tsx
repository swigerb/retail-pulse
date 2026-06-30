import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import ConversationExport from '../components/observability/ConversationExport';
import type { ExportSession } from '../types';

const mockSessions = [
  {
    sessionId: 'session-missing-fields',
    startTime: '2026-05-13T10:00:00Z',
    messageCount: 3,
  },
] as unknown as ExportSession[];

vi.mock('../services/observabilityApi', () => ({
  fetchExportSessions: vi.fn(() => Promise.resolve(mockSessions)),
  fetchExportPreview: vi.fn(),
  exportSession: vi.fn(),
}));

const wrap = (ui: React.ReactNode) => (
  <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>
);

describe('ConversationExport', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders sessions with missing optional backend fields without crashing', async () => {
    render(wrap(<ConversationExport />));

    await waitFor(() => {
      expect(screen.getByTestId('export-row-session-missing-fields')).toBeInTheDocument();
    });

    expect(screen.getByText('0')).toBeInTheDocument();
  });
});
