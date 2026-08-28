import { useState, useEffect, useCallback } from 'react';
import { makeStyles, Button, Badge } from '@fluentui/react-components';
import { Delete16Regular, Search16Regular } from '@fluentui/react-icons';
import { KB_COLORS } from '../../constants/agentRouting';
import type { KBDocument, KBSearchResult, KnowledgeProviderSnapshot } from '../../types';
import {
  fetchDocuments,
  deleteDocument,
  searchKnowledgeBase,
  fetchKnowledgeProviderSnapshot,
} from '../../services/knowledgeApi';
import DocumentUpload from './DocumentUpload';
import SearchResults from './SearchResults';
import KnowledgeStats from './KnowledgeStats';
import ProviderInfoCard from './ProviderInfoCard';
import AgentBindingsPanel from './AgentBindingsPanel';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    overflow: 'auto',
    padding: '24px',
    backgroundColor: 'var(--color-bg)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    marginBottom: '20px',
    flexWrap: 'wrap',
  },
  title: {
    fontSize: '22px',
    fontWeight: '700',
    color: '#06b6d4',
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    letterSpacing: '-0.5px',
  },
  subtitle: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    fontWeight: '500',
  },
  searchBar: {
    display: 'flex',
    gap: '8px',
    marginBottom: '20px',
  },
  searchInput: {
    flex: 1,
    padding: '8px 14px',
    borderRadius: '8px',
    border: '1px solid var(--color-border)',
    backgroundColor: 'var(--color-surface)',
    color: 'var(--color-text)',
    fontSize: '14px',
    outline: 'none',
  },
  sections: {
    display: 'grid',
    gridTemplateColumns: '1fr 320px',
    gap: '20px',
    '@media (max-width: 900px)': {
      gridTemplateColumns: '1fr',
    },
  },
  mainSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  sidebar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  docList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  docCard: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.06)',
    transition: 'background-color 0.15s',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.06)',
    },
  },
  docIcon: {
    fontSize: '20px',
    flexShrink: 0,
  },
  docInfo: {
    flex: 1,
    minWidth: 0,
  },
  docTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  docMeta: {
    display: 'flex',
    gap: '8px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    marginTop: '2px',
  },
  sectionTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    marginBottom: '8px',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
    borderRadius: '8px',
    backgroundColor: 'rgba(255,255,255,0.02)',
    border: '1px dashed rgba(255,255,255,0.1)',
  },
  error: {
    padding: '12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(239,68,68,0.1)',
    border: '1px solid rgba(239,68,68,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
    marginBottom: '12px',
  },
  providerRow: {
    marginBottom: '20px',
  },
  disabledState: {
    padding: '32px',
    borderRadius: '12px',
    textAlign: 'center',
    color: 'var(--color-text-muted, #94a3b8)',
    fontSize: '13px',
    lineHeight: '1.6',
    backgroundColor: 'var(--color-surface, rgba(255,255,255,0.03))',
    border: '1px dashed var(--color-border, rgba(255,255,255,0.15))',
  },
});

const SOURCE_ICONS: Record<string, string> = {
  internal: '📝',
  upload: '📎',
};

export default function KnowledgeBasePanel() {
  const styles = useStyles();
  const [documents, setDocuments] = useState<KBDocument[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<KBSearchResult[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<KnowledgeProviderSnapshot | null>(null);
  const [snapshotLoaded, setSnapshotLoaded] = useState(false);

  const loadDocuments = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const docs = await fetchDocuments();
      setDocuments(docs);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load documents');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadSnapshot = useCallback(async () => {
    try {
      const snap = await fetchKnowledgeProviderSnapshot();
      setSnapshot(snap);
    } catch {
      // A snapshot fetch failure never blocks the panel — the document list,
      // search, and stats still render. The provider info card is simply
      // omitted until a subsequent request succeeds.
      setSnapshot(null);
    } finally {
      setSnapshotLoaded(true);
    }
  }, []);

  useEffect(() => { loadDocuments(); }, [loadDocuments]);
  useEffect(() => { loadSnapshot(); }, [loadSnapshot]);

  const handleSearch = useCallback(async () => {
    if (!searchQuery.trim()) {
      setSearchResults(null);
      return;
    }
    setSearching(true);
    try {
      const results = await searchKnowledgeBase(searchQuery);
      setSearchResults(results);
    } catch {
      setSearchResults([]);
    } finally {
      setSearching(false);
    }
  }, [searchQuery]);

  const handleDelete = useCallback(async (id: string) => {
    try {
      await deleteDocument(id);
      setDocuments(prev => prev.filter(d => d.id !== id));
      // Refresh the provider snapshot so quota usage stays in sync after a
      // successful delete without a full page reload.
      loadSnapshot();
    } catch {
      setError('Failed to delete document');
    }
  }, [loadSnapshot]);

  const handleUploadComplete = useCallback(() => {
    loadDocuments();
    loadSnapshot();
  }, [loadDocuments, loadSnapshot]);

  const allBindingsDisabled = snapshot !== null
    && snapshot.bindings.length > 0
    && snapshot.bindings.every(b => !b.enabled);

  return (
    <div className={styles.container} data-testid="knowledge-base-panel">
      <div className={styles.header}>
        <div>
          <div className={styles.title}>📚 Knowledge Base</div>
          <div className={styles.subtitle}>Document Management & RAG Context</div>
        </div>
      </div>

      {error && <div className={styles.error} data-testid="kb-error">⚠️ {error}</div>}

      {snapshot && (
        <div className={styles.providerRow}>
          <ProviderInfoCard
            provider={snapshot.provider}
            degradation={snapshot.degradation}
            quotas={snapshot.quotas}
            usage={snapshot.usage}
          />
        </div>
      )}

      {snapshotLoaded && allBindingsDisabled && (
        <div className={styles.disabledState} data-testid="kb-disabled">
          🚫 Knowledge retrieval is disabled for every configured agent. No
          documents will be returned during chat until at least one agent has
          <code> use_knowledge_base: true</code>.
        </div>
      )}

      <div className={styles.searchBar}>
        <input
          className={styles.searchInput}
          placeholder="Search knowledge base..."
          value={searchQuery}
          onChange={e => setSearchQuery(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && handleSearch()}
          data-testid="kb-search-input"
        />
        <Button
          appearance="primary"
          icon={<Search16Regular />}
          onClick={handleSearch}
          disabled={searching}
          data-testid="kb-search-button"
          style={{ backgroundColor: KB_COLORS.primary }}
        >
          {searching ? 'Searching...' : 'Search'}
        </Button>
      </div>

      {searchResults !== null && (
        <div style={{ marginBottom: '20px' }}>
          <SearchResults
            results={searchResults}
            query={searchQuery}
            relevanceKind={snapshot?.provider.relevance}
            scoreSemantics={snapshot?.provider.scoreSemantics}
          />
        </div>
      )}

      <div className={styles.sections}>
        <div className={styles.mainSection}>
          <DocumentUpload
            onUploadComplete={handleUploadComplete}
            provider={snapshot?.provider ?? null}
          />

          <div>
            <div className={styles.sectionTitle}>
              📑 Documents
              <Badge appearance="filled" style={{ background: 'rgba(6,182,212,0.15)', color: '#67e8f9' }}>
                {documents.length}
              </Badge>
            </div>

            {loading ? (
              <div className={styles.empty}>⏳ Loading documents...</div>
            ) : documents.length === 0 ? (
              <div className={styles.empty} data-testid="docs-empty">
                No documents in the knowledge base yet. Upload your first document above.
              </div>
            ) : (
              <div className={styles.docList} data-testid="document-list">
                {documents.map(doc => (
                  <div key={doc.id} className={styles.docCard} data-testid="document-card">
                    <span className={styles.docIcon}>{SOURCE_ICONS[doc.source] || '📄'}</span>
                    <div className={styles.docInfo}>
                      <div className={styles.docTitle}>{doc.title}</div>
                      <div className={styles.docMeta}>
                        <span>{doc.source}</span>
                        <span>•</span>
                        <span>{doc.chunkCount} chunks</span>
                        <span>•</span>
                        <span>{new Date(doc.ingestedAt).toLocaleDateString()}</span>
                      </div>
                    </div>
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<Delete16Regular />}
                      onClick={() => handleDelete(doc.id)}
                      aria-label={`Delete ${doc.title}`}
                      data-testid="delete-doc-btn"
                    />
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        <div className={styles.sidebar}>
          {snapshot && (
            <AgentBindingsPanel
              bindings={snapshot.bindings}
              sources={snapshot.sources}
            />
          )}
          <KnowledgeStats />
        </div>
      </div>
    </div>
  );
}
