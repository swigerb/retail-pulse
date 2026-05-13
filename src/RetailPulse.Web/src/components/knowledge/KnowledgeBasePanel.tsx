import { useState, useEffect, useCallback } from 'react';
import { makeStyles, Button, Badge } from '@fluentui/react-components';
import { Delete16Regular, Search16Regular } from '@fluentui/react-icons';
import { KB_COLORS } from '../../constants/agentRouting';
import type { KBDocument, KBSearchResult } from '../../types';
import { fetchDocuments, deleteDocument, searchKnowledgeBase } from '../../services/knowledgeApi';
import DocumentUpload from './DocumentUpload';
import SearchResults from './SearchResults';
import KnowledgeStats from './KnowledgeStats';

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
});

const SOURCE_ICONS: Record<string, string> = {
  markdown: '📝',
  text: '📄',
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

  useEffect(() => { loadDocuments(); }, [loadDocuments]);

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
    } catch {
      setError('Failed to delete document');
    }
  }, []);

  const handleUploadComplete = useCallback((doc: KBDocument) => {
    setDocuments(prev => [doc, ...prev]);
  }, []);

  return (
    <div className={styles.container} data-testid="knowledge-base-panel">
      <div className={styles.header}>
        <div>
          <div className={styles.title}>📚 Knowledge Base</div>
          <div className={styles.subtitle}>Document Management & RAG Context</div>
        </div>
      </div>

      {error && <div className={styles.error} data-testid="kb-error">⚠️ {error}</div>}

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
          style={{ backgroundColor: KB_COLORS.primary }}
        >
          {searching ? 'Searching...' : 'Search'}
        </Button>
      </div>

      {searchResults !== null && (
        <div style={{ marginBottom: '20px' }}>
          <SearchResults results={searchResults} query={searchQuery} />
        </div>
      )}

      <div className={styles.sections}>
        <div className={styles.mainSection}>
          <DocumentUpload onUploadComplete={handleUploadComplete} />

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
                    <span className={styles.docIcon}>{SOURCE_ICONS[doc.sourceType] || '📄'}</span>
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
          <KnowledgeStats />
        </div>
      </div>
    </div>
  );
}
