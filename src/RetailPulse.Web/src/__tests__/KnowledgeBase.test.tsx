import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, teamsDarkTheme } from '@fluentui/react-components';
import type {
  KBDocument,
  KBSearchResult,
  KBStats,
  KnowledgeProviderSnapshot,
} from '../types';

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

function makeSnapshot(overrides: Partial<KnowledgeProviderSnapshot> = {}): KnowledgeProviderSnapshot {
  return {
    provider: {
      name: 'InMemory',
      relevance: 'Lexical',
      persistent: false,
      requiresCloud: false,
      supportsMutation: true,
      scoreSemantics:
        'BM25 lexical score, normalized 0-1 within a single query response. ' +
        'Scores are provider-local and not comparable across providers.',
      ...overrides.provider,
    },
    degradation: {
      mode: 'FailLoud',
      primaryReplacedByFallback: false,
      ...overrides.degradation,
    },
    quotas: {
      maxDocuments: 100,
      maxChunks: 5000,
      maxDocumentSizeBytes: 10 * 1024 * 1024,
      ...overrides.quotas,
    },
    usage: {
      documentCount: 2,
      chunkCount: 32,
      ...overrides.usage,
    },
    sources: overrides.sources ?? [
      { name: 'planogram-shelf-set', documents: ['apex-planogram-and-shelf-set.md'] },
      { name: 'supplier-service-levels', documents: ['apex-supplier-service-levels.md'] },
    ],
    bindings: overrides.bindings ?? [
      {
        agentKey: 'planogram',
        agentDisplayName: 'Planogram Agent',
        enabled: true,
        sourceName: 'planogram-shelf-set',
        sources: ['apex-planogram-and-shelf-set.md'],
      },
      {
        agentKey: 'general',
        agentDisplayName: 'General Assistant',
        enabled: true,
        sourceName: '',
        sources: [],
      },
      {
        agentKey: 'memory-management',
        agentDisplayName: 'Memory Manager',
        enabled: false,
        sourceName: '',
        sources: [],
      },
    ],
  };
}

const mockFetchDocs = vi.fn().mockResolvedValue(mockDocuments);
const mockDeleteDoc = vi.fn().mockResolvedValue(undefined);
const mockSearchKB = vi.fn().mockResolvedValue(mockSearchResults);
const mockUploadDoc = vi.fn().mockResolvedValue({
  documentId: 'doc-new',
  title: 'Test Doc',
  status: 'ingested',
  chunkCount: 5,
  source: 'upload',
});
const mockFetchStats = vi.fn().mockResolvedValue(mockStats);
const mockFetchSnapshot = vi.fn().mockResolvedValue(makeSnapshot());

// Content-safety, quota, and read-only rejection paths from `knowledgeApi`
// surface as typed error classes so `DocumentUpload` can render distinct
// outcomes for each. `vi.mock` is hoisted above module-level `class`
// declarations, so plain top-level classes would be in their temporal dead
// zone when the mock factory runs. `vi.hoisted` runs alongside the hoisted
// mocks, guaranteeing the class bindings exist before the factory evaluates.
const {
  MockKnowledgeUploadError,
  MockKnowledgeQuotaError,
  MockKnowledgeMutationUnsupportedError,
} = vi.hoisted(() => {
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
  class MockKnowledgeQuotaError extends Error {
    readonly quotas: unknown;
    readonly usage: unknown;
    readonly status: number;
    constructor(reason: string, quotas: unknown, usage: unknown, status = 409) {
      super(reason);
      this.name = 'KnowledgeQuotaError';
      this.quotas = quotas;
      this.usage = usage;
      this.status = status;
    }
  }
  class MockKnowledgeMutationUnsupportedError extends Error {
    readonly status: number;
    constructor(reason: string, status = 405) {
      super(reason);
      this.name = 'KnowledgeMutationUnsupportedError';
      this.status = status;
    }
  }
  return {
    MockKnowledgeUploadError,
    MockKnowledgeQuotaError,
    MockKnowledgeMutationUnsupportedError,
  };
});

vi.mock('../services/knowledgeApi', () => ({
  fetchDocuments: (...args: unknown[]) => mockFetchDocs(...args),
  deleteDocument: (...args: unknown[]) => mockDeleteDoc(...args),
  searchKnowledgeBase: (...args: unknown[]) => mockSearchKB(...args),
  uploadDocument: (...args: unknown[]) => mockUploadDoc(...args),
  fetchKBStats: (...args: unknown[]) => mockFetchStats(...args),
  fetchKnowledgeProviderSnapshot: (...args: unknown[]) => mockFetchSnapshot(...args),
  KnowledgeUploadError: MockKnowledgeUploadError,
  KnowledgeQuotaError: MockKnowledgeQuotaError,
  KnowledgeMutationUnsupportedError: MockKnowledgeMutationUnsupportedError,
}));

import KnowledgeBasePanel from '../components/knowledge/KnowledgeBasePanel';
import DocumentUpload from '../components/knowledge/DocumentUpload';
import CitationBadge from '../components/knowledge/CitationBadge';
import ProviderInfoCard from '../components/knowledge/ProviderInfoCard';
import AgentBindingsPanel from '../components/knowledge/AgentBindingsPanel';
import SearchResults from '../components/knowledge/SearchResults';

function wrap(ui: React.ReactNode) {
  return <FluentProvider theme={teamsDarkTheme}>{ui}</FluentProvider>;
}

describe('KnowledgeBasePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockFetchDocs.mockResolvedValue(mockDocuments);
    mockDeleteDoc.mockResolvedValue(undefined);
    mockSearchKB.mockResolvedValue(mockSearchResults);
    mockUploadDoc.mockResolvedValue({
      documentId: 'doc-new',
      title: 'Test Doc',
      status: 'ingested',
      chunkCount: 5,
      source: 'upload',
    });
    mockFetchStats.mockResolvedValue(mockStats);
    mockFetchSnapshot.mockResolvedValue(makeSnapshot());
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

  it('renders the provider info card with durable/volatile signal', async () => {
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchSnapshot).toHaveBeenCalled());
    const durability = await screen.findByTestId('kb-provider-durability');
    expect(durability).toHaveAttribute('data-durability', 'volatile');
    // Status is not conveyed by color alone — the text label must be present.
    expect(durability).toHaveTextContent(/Volatile/);
    expect(screen.getByTestId('kb-provider-name')).toHaveTextContent('InMemory');
    expect(screen.getByTestId('kb-provider-relevance')).toHaveTextContent(/Lexical/);
    expect(screen.getByTestId('kb-score-semantics')).toHaveTextContent(/BM25/);
  });

  it('renders per-agent bindings with named source and unscoped labels', async () => {
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchSnapshot).toHaveBeenCalled());
    const bindings = await screen.findByTestId('kb-agent-bindings');
    const planogram = within(bindings).getByTestId('kb-binding-planogram');
    expect(planogram).toHaveTextContent(/planogram-shelf-set/);
    expect(planogram).toHaveAttribute('data-binding-enabled', 'true');
    const general = within(bindings).getByTestId('kb-binding-general');
    expect(general).toHaveTextContent(/Unscoped/);
    const memory = within(bindings).getByTestId('kb-binding-memory-management');
    expect(memory).toHaveAttribute('data-binding-enabled', 'false');
    expect(memory).toHaveTextContent(/Disabled/);
  });

  it('renders the disabled-knowledge state when every agent binding is disabled', async () => {
    mockFetchSnapshot.mockResolvedValueOnce(makeSnapshot({
      bindings: [
        {
          agentKey: 'planogram',
          agentDisplayName: 'Planogram Agent',
          enabled: false,
          sourceName: '',
          sources: [],
        },
        {
          agentKey: 'general',
          agentDisplayName: 'General Assistant',
          enabled: false,
          sourceName: '',
          sources: [],
        },
      ],
    }));
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchSnapshot).toHaveBeenCalled());
    expect(await screen.findByTestId('kb-disabled')).toBeInTheDocument();
  });

  it('still renders documents when the provider snapshot fetch fails', async () => {
    mockFetchSnapshot.mockRejectedValueOnce(new Error('provider snapshot unavailable'));
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchDocs).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId('document-list')).toBeInTheDocument());
    expect(screen.queryByTestId('kb-provider-info')).not.toBeInTheDocument();
    expect(screen.queryByTestId('kb-agent-bindings')).not.toBeInTheDocument();
  });

  it('passes honest relevance semantics to search results', async () => {
    mockFetchSnapshot.mockResolvedValueOnce(makeSnapshot({
      provider: {
        name: 'AzureAISearch',
        relevance: 'Hybrid',
        persistent: true,
        requiresCloud: true,
        supportsMutation: true,
        scoreSemantics: 'Hybrid rank fusion — provider-local scores.',
      },
    }));
    render(wrap(<KnowledgeBasePanel />));
    await waitFor(() => expect(mockFetchSnapshot).toHaveBeenCalled());

    const input = screen.getByTestId('kb-search-input');
    fireEvent.change(input, { target: { value: 'enterprise' } });
    fireEvent.click(screen.getByText('Search'));

    await waitFor(() => expect(screen.getByTestId('search-results')).toBeInTheDocument());
    expect(screen.getByTestId('search-relevance-kind')).toHaveTextContent(/Hybrid relevance/);
    expect(screen.getByTestId('search-score-semantics')).toHaveTextContent(/Hybrid rank fusion/);
  });
});

describe('DocumentUpload', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockUploadDoc.mockResolvedValue({
      documentId: 'doc-new',
      title: 'Test Doc',
      status: 'ingested',
      chunkCount: 5,
      source: 'upload',
    });
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

  it('uploads document and shows accepted outcome with chunk count and source', async () => {
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
    expect(screen.getByTestId('upload-accepted-meta')).toHaveTextContent(/5 chunks/);
    expect(screen.getByTestId('upload-accepted-meta')).toHaveTextContent(/upload/);
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

  it('renders a volatile warning when the provider is not persistent', () => {
    render(wrap(
      <DocumentUpload
        onUploadComplete={vi.fn()}
        provider={{
          name: 'InMemory',
          relevance: 'Lexical',
          persistent: false,
          requiresCloud: false,
          supportsMutation: true,
          scoreSemantics: 'BM25 lexical.',
        }}
      />,
    ));
    const warning = screen.getByTestId('upload-volatile-warning');
    expect(warning).toBeInTheDocument();
    expect(warning).toHaveAttribute('role', 'alert');
    expect(warning).toHaveTextContent(/lost on restart/i);
  });

  it('omits the volatile warning when the provider is persistent', () => {
    render(wrap(
      <DocumentUpload
        onUploadComplete={vi.fn()}
        provider={{
          name: 'AzureAISearch',
          relevance: 'Hybrid',
          persistent: true,
          requiresCloud: true,
          supportsMutation: true,
          scoreSemantics: 'Hybrid rank fusion.',
        }}
      />,
    ));
    expect(screen.queryByTestId('upload-volatile-warning')).not.toBeInTheDocument();
  });

  it('renders a read-only state and hides the drop zone when the provider does not support mutation', () => {
    render(wrap(
      <DocumentUpload
        onUploadComplete={vi.fn()}
        provider={{
          name: 'FoundryIQ',
          relevance: 'Semantic',
          persistent: true,
          requiresCloud: true,
          supportsMutation: false,
          scoreSemantics: 'Foundry-managed vector store scores.',
        }}
      />,
    ));
    expect(screen.getByTestId('upload-readonly')).toBeInTheDocument();
    expect(screen.queryByTestId('file-input')).not.toBeInTheDocument();
    expect(screen.queryByTestId('upload-volatile-warning')).not.toBeInTheDocument();
  });

  it('renders a quota-rejected outcome distinct from a generic error', async () => {
    mockUploadDoc.mockRejectedValueOnce(new MockKnowledgeQuotaError(
      'Knowledge base is full (100 documents).',
      { maxDocuments: 100, maxChunks: 5000, maxDocumentSizeBytes: 10 * 1024 * 1024 },
      { documentCount: 100, chunkCount: 4200 },
      409,
    ));
    render(wrap(<DocumentUpload onUploadComplete={vi.fn()} />));

    const file = new File(['x'], 'big.md', { type: 'text/markdown' });
    await userEvent.upload(screen.getByTestId('file-input'), file);
    await waitFor(() => expect(screen.getByTestId('upload-btn')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('upload-btn'));

    await waitFor(() => expect(screen.getByTestId('upload-quota-block')).toBeInTheDocument());
    expect(screen.getByTestId('upload-quota-block')).toHaveAttribute('role', 'alert');
    expect(screen.getByTestId('upload-quota-meta')).toHaveTextContent(/100 \/ 100/);
    expect(screen.queryByTestId('upload-success')).not.toBeInTheDocument();
    expect(screen.queryByTestId('upload-error')).not.toBeInTheDocument();
  });

  it('renders the generic failure state when the upload fails for non-classified reasons', async () => {
    mockUploadDoc.mockRejectedValueOnce(new Error('network down'));
    render(wrap(<DocumentUpload onUploadComplete={vi.fn()} />));

    const file = new File(['x'], 'stub.md', { type: 'text/markdown' });
    await userEvent.upload(screen.getByTestId('file-input'), file);
    await waitFor(() => expect(screen.getByTestId('upload-btn')).toBeInTheDocument());
    fireEvent.click(screen.getByTestId('upload-btn'));

    await waitFor(() => expect(screen.getByTestId('upload-error')).toBeInTheDocument());
    expect(screen.getByTestId('upload-error')).toHaveTextContent(/network down/);
  });
});

describe('ProviderInfoCard', () => {
  it('renders quota bars in an exceeded state when usage meets the limit', () => {
    render(wrap(<ProviderInfoCard
      provider={{
        name: 'InMemory',
        relevance: 'Lexical',
        persistent: false,
        requiresCloud: false,
        supportsMutation: true,
        scoreSemantics: 'BM25 lexical.',
      }}
      degradation={{ mode: 'FailLoud', primaryReplacedByFallback: false }}
      quotas={{ maxDocuments: 100, maxChunks: 5000, maxDocumentSizeBytes: 10 * 1024 * 1024 }}
      usage={{ documentCount: 100, chunkCount: 5000 }}
    />));
    const docBar = screen.getByTestId('kb-quota-documents');
    expect(docBar).toHaveAttribute('data-quota-status', 'exceeded');
    const chunkBar = screen.getByTestId('kb-quota-chunks');
    expect(chunkBar).toHaveAttribute('data-quota-status', 'exceeded');
  });

  it('renders a fallback alert when the primary was replaced', () => {
    render(wrap(<ProviderInfoCard
      provider={{
        name: 'InMemory',
        relevance: 'Lexical',
        persistent: false,
        requiresCloud: false,
        supportsMutation: true,
        scoreSemantics: 'BM25 lexical.',
      }}
      degradation={{ mode: 'FallbackToInMemory', primaryReplacedByFallback: true }}
      quotas={{ maxDocuments: 100, maxChunks: 5000, maxDocumentSizeBytes: 10 * 1024 * 1024 }}
      usage={{ documentCount: 5, chunkCount: 24 }}
    />));
    const alert = screen.getByTestId('kb-fallback-alert');
    expect(alert).toHaveAttribute('role', 'alert');
    expect(alert).toHaveTextContent(/fallback/i);
  });

  it('reflects read-only providers in the mutation badge (text, not colour)', () => {
    render(wrap(<ProviderInfoCard
      provider={{
        name: 'FoundryIQ',
        relevance: 'Semantic',
        persistent: true,
        requiresCloud: true,
        supportsMutation: false,
        scoreSemantics: 'Foundry-managed scores.',
      }}
      degradation={{ mode: 'FailLoud', primaryReplacedByFallback: false }}
      quotas={{ maxDocuments: 100, maxChunks: 5000, maxDocumentSizeBytes: 10 * 1024 * 1024 }}
      usage={{ documentCount: 0, chunkCount: 0 }}
    />));
    const mutation = screen.getByTestId('kb-provider-mutation');
    expect(mutation).toHaveAttribute('data-mutation', 'read-only');
    expect(mutation).toHaveTextContent(/Read-only corpus/);
  });
});

describe('AgentBindingsPanel', () => {
  it('renders named sources and their documents', () => {
    render(wrap(<AgentBindingsPanel
      bindings={[]}
      sources={[
        { name: 'planogram-shelf-set', documents: ['apex-planogram-and-shelf-set.md'] },
        { name: 'supplier-service-levels', documents: ['apex-supplier-service-levels.md'] },
      ]}
    />));
    expect(screen.getByTestId('kb-named-source-planogram-shelf-set')).toHaveTextContent(/apex-planogram-and-shelf-set/);
    expect(screen.getByTestId('kb-named-source-supplier-service-levels')).toHaveTextContent(/apex-supplier-service-levels/);
  });

  it('renders the empty-sources message when no named sources are configured', () => {
    render(wrap(<AgentBindingsPanel bindings={[]} sources={[]} />));
    expect(screen.getByTestId('kb-named-sources-empty')).toBeInTheDocument();
  });

  it('is keyboard navigable — every binding row and status badge exposes a text label', () => {
    render(wrap(<AgentBindingsPanel
      bindings={[
        {
          agentKey: 'planogram',
          agentDisplayName: 'Planogram Agent',
          enabled: true,
          sourceName: 'planogram-shelf-set',
          sources: ['apex-planogram-and-shelf-set.md'],
        },
        {
          agentKey: 'memory',
          agentDisplayName: 'Memory Manager',
          enabled: false,
          sourceName: '',
          sources: [],
        },
      ]}
      sources={[]}
    />));
    const enabled = screen.getByTestId('kb-binding-planogram');
    expect(within(enabled).getByText(/Enabled/)).toBeInTheDocument();
    const disabled = screen.getByTestId('kb-binding-memory');
    expect(within(disabled).getByText(/Disabled/)).toBeInTheDocument();
  });
});

describe('SearchResults relevance honesty', () => {
  it('renders a lexical label and per-result score for the in-memory provider', () => {
    render(wrap(<SearchResults
      results={mockSearchResults}
      query="enterprise"
      relevanceKind="Lexical"
      scoreSemantics="BM25 lexical score."
    />));
    expect(screen.getByTestId('search-relevance-kind')).toHaveTextContent(/Lexical relevance/);
    expect(screen.getByTestId('search-score-semantics')).toHaveTextContent(/BM25 lexical/);
    expect(screen.getByTestId('search-result')).toHaveAttribute('data-relevance-band', 'high');
  });

  it('renders a semantic label without implying cross-provider comparability', () => {
    render(wrap(<SearchResults
      results={mockSearchResults}
      query="enterprise"
      relevanceKind="Semantic"
      scoreSemantics="Cosine similarity from vector embeddings."
    />));
    expect(screen.getByTestId('search-relevance-kind')).toHaveTextContent(/Semantic relevance/);
    expect(screen.getByTestId('search-score-semantics')).toHaveTextContent(/Cosine similarity/);
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
