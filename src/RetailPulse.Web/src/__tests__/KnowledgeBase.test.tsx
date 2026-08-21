import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type { KBDocument, KBSearchResult, KBStats } from '../types';

const mockDocuments: KBDocument[] = [
  {
    id: 'doc-1',
    title: 'Sales Playbook 2026',
    source: 'internal',
    ingestedAt: '2026-05-10T10:00:00Z',
    chunkCount: 24,
  },
  {
    id: 'doc-2',
    title: 'Competitor Analysis Notes',
    source: 'upload',
    ingestedAt: '2026-05-12T14:00:00Z',
    chunkCount: 8,
  },
];

const mockStats: KBStats = {
  documentCount: 2,
  chunkCount: 32,
  averageChunksPerDocument: 16,
};

const mockSearchResults: KBSearchResult[] = [
  {
    documentId: 'doc-1',
    title: 'Sales Playbook 2026',
    chunk: 'When approaching enterprise clients in the Southeast...',
    score: 0.92,
    source: 'internal',
    chunkIndex: 3,
  },
];

const mockFetchDocs = vi.fn().mockResolvedValue(mockDocuments);
const mockDeleteDoc = vi.fn().mockResolvedValue(undefined);
const mockSearchKB = vi.fn().mockResolvedValue(mockSearchResults);
const mockUploadDoc = vi.fn().mockResolvedValue({ documentId: 'doc-new', title: 'Test Doc', status: 'ingested' });
const mockFetchStats = vi.fn().mockResolvedValue(mockStats);

// Content-safety-flavored upload rejection surfaces as a KnowledgeUploadError
// so DocumentUpload can render a KnowledgeIngestionBlock in place of a raw
// error string. Tests use a lightweight stand-in that matches the shape and
// participates in `instanceof` checks.
class MockKnowledgeUploadError extends Error {
  readonly display: unknown;
  readonly status: number;
  constructor(display: unknown, status = 422) {
    super('safety rejection');
    this.name = 'KnowledgeUploadError';
    this.display = display;
    this.status = status;
  }
}

vi.mock('../services/knowledgeApi', () => ({
  fetchDocuments: (...args: unknown[]) => mockFetchDocs(...args),
  deleteDocument: (...args: unknown[]) => mockDeleteDoc(...args),
  searchKnowledgeBase: (...args: unknown[]) => mockSearchKB(...args),
  uploadDocument: (...args: unknown[]) => mockUploadDoc(...args),
  fetchKBStats: (...args: unknown[]) => mockFetchStats(...args),
  KnowledgeUploadError: MockKnowledgeUploadError,
}));

import KnowledgeBasePanel from '../components/knowledge/KnowledgeBasePanel';
import DocumentUpload from '../components/knowledge/DocumentUpload';
import CitationBadge from '../components/knowledge/CitationBadge';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('KnowledgeBasePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders knowledge base panel with title and search', async () => {
    render(wrap(<KnowledgeBasePanel />));
    expect(screen.getByTestId('knowledge-base-panel')).toBeInTheDocument();
    expect(screen.getByText(/Knowledge Base/)).toBeInTheDocument();
    expect(screen.getByTestId('kb-search-input')).toBeInTheDocument();
  });

  it('loads and displays documents', async () => {
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => {
      expect(mockFetchDocs).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(screen.getByTestId('document-list')).toBeInTheDocument();
    });
    const cards = screen.getAllByTestId('document-card');
    expect(cards).toHaveLength(2);
    expect(screen.getAllByText('Sales Playbook 2026').length).toBeGreaterThanOrEqual(1);
  });

  it('performs search when button is clicked', async () => {
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchDocs).toHaveBeenCalled());

    const input = screen.getByTestId('kb-search-input');
    fireEvent.change(input, { target: { value: 'enterprise clients' } });
    fireEvent.click(screen.getByText('Search'));

    await waitFor(() => {
      expect(mockSearchKB).toHaveBeenCalledWith('enterprise clients');
    });
  });

  it('shows error state when fetch fails', async () => {
    mockFetchDocs.mockRejectedValueOnce(new Error('Network error'));
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => {
      expect(screen.getByTestId('kb-error')).toBeInTheDocument();
    });
  });
});

describe('DocumentUpload', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders upload zone with accepted formats', () => {
    render(wrap(<DocumentUpload onUploadComplete={vi.fn()} />));
    expect(screen.getByTestId('document-upload')).toBeInTheDocument();
    expect(screen.getByText('.md')).toBeInTheDocument();
    expect(screen.getByText('.txt')).toBeInTheDocument();
  });

  it('shows file input and title field after file selection', async () => {
    const onComplete = vi.fn();
    render(wrap(<DocumentUpload onUploadComplete={onComplete} />));

    const file = new File(['test content'], 'test-doc.md', { type: 'text/markdown' });
    const input = screen.getByTestId('file-input');
    await userEvent.upload(input, file);

    await waitFor(() => {
      expect(screen.getByTestId('title-input')).toBeInTheDocument();
    });
    expect(screen.getByTestId('upload-btn')).toBeInTheDocument();
  });

  it('uploads document and shows success state', async () => {
    const onComplete = vi.fn();
    render(wrap(<DocumentUpload onUploadComplete={onComplete} />));

    const file = new File(['test content'], 'test-doc.md', { type: 'text/markdown' });
    const input = screen.getByTestId('file-input');
    await userEvent.upload(input, file);

    await waitFor(() => expect(screen.getByTestId('upload-btn')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('upload-btn'));

    await waitFor(() => {
      expect(mockUploadDoc).toHaveBeenCalled();
    });
    await waitFor(() => {
      expect(screen.getByTestId('upload-success')).toBeInTheDocument();
    });
    expect(onComplete).toHaveBeenCalled();
  });

  it('renders a KnowledgeIngestionBlock when uploadDocument throws a safety rejection', async () => {
    const safetyDisplay = {
      stage: 'ingestion' as const,
      family: 'model' as const,
      reason: 'This document was quarantined by our content safety layer during ingestion.',
      categoryLabel: 'Hateful content',
      categoryName: 'Hate' as const,
      severityLabel: 'high' as const,
      decision: 'Blocked' as const,
      modelBased: true,
    };
    mockUploadDoc.mockRejectedValueOnce(new MockKnowledgeUploadError(safetyDisplay, 422));

    const onComplete = vi.fn();
    render(wrap(<DocumentUpload onUploadComplete={onComplete} />));

    const file = new File(['content'], 'quarantined.md', { type: 'text/markdown' });
    const input = screen.getByTestId('file-input');
    await userEvent.upload(input, file);
    await waitFor(() => expect(screen.getByTestId('upload-btn')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('upload-btn'));

    await waitFor(() => {
      expect(screen.getByTestId('upload-safety-block')).toBeInTheDocument();
    });
    expect(screen.getByTestId('knowledge-ingestion-block')).toHaveAttribute(
      'data-safety-stage',
      'ingestion',
    );
    expect(onComplete).not.toHaveBeenCalled();
    // Generic error message must NOT render when safety block is shown.
    expect(screen.queryByTestId('upload-error')).not.toBeInTheDocument();
  });
});

describe('CitationBadge', () => {
  const citation = {
    sourceName: 'Playbook',
    sourceTitle: 'Sales Playbook 2026',
    chunkPreview: 'When approaching enterprise clients in the Southeast region...',
    relevanceScore: 0.92,
  };

  it('renders citation pill with source name and relevance', () => {
    render(wrap(<CitationBadge citation={citation} />));
    expect(screen.getByTestId('citation-badge')).toBeInTheDocument();
    expect(screen.getByText('📖 Playbook')).toBeInTheDocument();
    expect(screen.getByText('92%')).toBeInTheDocument();
  });

  it('shows tooltip on hover', async () => {
    render(wrap(<CitationBadge citation={citation} />));
    fireEvent.mouseEnter(screen.getByTestId('citation-badge'));
    expect(screen.getByTestId('citation-tooltip')).toBeInTheDocument();
    expect(screen.getByText('Sales Playbook 2026')).toBeInTheDocument();
  });

  it('expands on click to show full content', () => {
    render(wrap(<CitationBadge citation={citation} />));
    fireEvent.click(screen.getByTestId('citation-badge'));
    expect(screen.getByTestId('citation-expanded')).toBeInTheDocument();
  });

  it('shows appropriate color for different relevance levels', () => {
    const lowCitation = { ...citation, relevanceScore: 0.3 };
    render(wrap(<CitationBadge citation={lowCitation} />));
    expect(screen.getByText('30%')).toBeInTheDocument();
  });
});
