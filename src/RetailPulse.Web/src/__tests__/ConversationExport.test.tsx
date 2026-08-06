import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import ConversationExport from '../components/observability/ConversationExport';
import type { ExportSession } from '../types';
import { exportSession } from '../services/observabilityApi';

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
  exportSession: vi.fn(() => Promise.resolve(new Blob(['# export'], { type: 'text/markdown' }))),
}));

const wrap = (ui: React.ReactNode) => (
  <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>
);

describe('ConversationExport', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom does not implement object URLs; the export handler creates one to
    // trigger a download, so stub them to keep the click path exercisable.
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:mock'),
      revokeObjectURL: vi.fn(),
    });
  });

  it('renders sessions with missing optional backend fields without crashing', async () => {
    render(wrap(<ConversationExport />));

    await waitFor(() => {
      expect(screen.getByTestId('export-row-session-missing-fields')).toBeInTheDocument();
    });

    expect(screen.getByText('0')).toBeInTheDocument();
  });

  it('opens the Fluent export menu and offers both formats (portal, not clipped)', async () => {
    const user = userEvent.setup();
    render(wrap(<ConversationExport />));

    await screen.findByTestId('export-btn-session-missing-fields');
    await user.click(screen.getByTestId('export-btn-session-missing-fields'));

    // MenuItems render in a Fluent portal (escaping the table's overflow clip),
    // so they must be discoverable in the document once the menu opens.
    expect(await screen.findByTestId('export-md-session-missing-fields')).toBeInTheDocument();
    expect(screen.getByTestId('export-json-session-missing-fields')).toBeInTheDocument();
  });

  it('exports Markdown when the Markdown item is chosen', async () => {
    const user = userEvent.setup();
    render(wrap(<ConversationExport />));

    await screen.findByTestId('export-btn-session-missing-fields');
    await user.click(screen.getByTestId('export-btn-session-missing-fields'));
    await user.click(await screen.findByTestId('export-md-session-missing-fields'));

    await waitFor(() => {
      expect(exportSession).toHaveBeenCalledWith('session-missing-fields', 'markdown');
    });
  });

  it('exports JSON when the JSON item is chosen', async () => {
    const user = userEvent.setup();
    render(wrap(<ConversationExport />));

    await screen.findByTestId('export-btn-session-missing-fields');
    await user.click(screen.getByTestId('export-btn-session-missing-fields'));
    await user.click(await screen.findByTestId('export-json-session-missing-fields'));

    await waitFor(() => {
      expect(exportSession).toHaveBeenCalledWith('session-missing-fields', 'json');
    });
  });

  it('exposes an accessible menu button and menu semantics', async () => {
    const user = userEvent.setup();
    render(wrap(<ConversationExport />));

    const trigger = await screen.findByTestId('export-btn-session-missing-fields');
    // The trigger is a real, labelled button that advertises a popup menu.
    expect(trigger.tagName).toBe('BUTTON');
    expect(trigger).toHaveAttribute('aria-haspopup');
    expect(trigger).toHaveAccessibleName(/export session/i);

    await user.click(trigger);

    // Opened menu exposes proper ARIA roles for assistive tech.
    const menu = await screen.findByRole('menu');
    expect(menu).toBeInTheDocument();
    expect(screen.getAllByRole('menuitem')).toHaveLength(2);
  });
});
