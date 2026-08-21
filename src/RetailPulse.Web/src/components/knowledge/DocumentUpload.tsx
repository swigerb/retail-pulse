import { useState, useCallback, useRef } from 'react';
import { makeStyles, Button } from '@fluentui/react-components';
import { KB_COLORS } from '../../constants/agentRouting';
import {
  uploadDocument,
  KnowledgeUploadError,
  KnowledgeQuotaError,
  KnowledgeMutationUnsupportedError,
} from '../../services/knowledgeApi';
import type {
  KnowledgeProviderInfo,
  KnowledgeQuotas,
  KnowledgeUsage,
  SafetyBlockDisplayModel,
} from '../../types';
import { KnowledgeIngestionBlock } from '../guardrails/KnowledgeIngestionBlock';

interface DocumentUploadProps {
  onUploadComplete: () => void;
  provider?: KnowledgeProviderInfo | null;
}

const ACCEPTED_FORMATS = ['.md', '.txt'];

const useStyles = makeStyles({
  wrapper: {
    padding: '20px',
    borderRadius: '12px',
    border: '2px dashed rgba(6,182,212,0.3)',
    backgroundColor: 'rgba(6,182,212,0.04)',
    textAlign: 'center',
    transition: 'all 0.2s ease',
    cursor: 'pointer',
  },
  wrapperDragOver: {
    transform: 'scale(1.01)',
  },
  wrapperReadOnly: {
    cursor: 'not-allowed',
    opacity: 0.85,
  },
  volatileWarning: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'center',
    gap: '8px',
    textAlign: 'left',
    padding: '10px 12px',
    marginBottom: '12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(245,158,11,0.10)',
    border: '1px solid var(--color-accent-warning, #f59e0b)',
    color: 'var(--color-accent-warning, #f59e0b)',
    fontSize: '12px',
    lineHeight: '1.5',
  },
  readOnlyBanner: {
    padding: '14px 16px',
    borderRadius: '8px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.04))',
    border: '1px solid var(--color-border, rgba(255,255,255,0.15))',
    color: 'var(--color-text, #ffffff)',
    fontSize: '13px',
    lineHeight: '1.5',
  },
  quotaBlock: {
    marginTop: '12px',
    padding: '10px 12px',
    borderRadius: '8px',
    backgroundColor: 'rgba(239,68,68,0.10)',
    border: '1px solid var(--color-accent-danger, #ef4444)',
    color: 'var(--color-accent-danger, #ef4444)',
    fontSize: '13px',
    lineHeight: '1.5',
    textAlign: 'left',
  },
  outcomeMeta: {
    marginTop: '4px',
    fontSize: '11px',
    color: 'var(--color-text-muted, #94a3b8)',
  },
  icon: {
    fontSize: '32px',
    marginBottom: '8px',
  },
  label: {
    fontSize: '14px',
    color: 'var(--color-text)',
    fontWeight: '500',
    marginBottom: '4px',
  },
  hint: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    marginBottom: '12px',
  },
  formats: {
    display: 'flex',
    justifyContent: 'center',
    gap: '6px',
    marginBottom: '12px',
  },
  formatBadge: {
    fontSize: '11px',
    padding: '2px 8px',
    borderRadius: '4px',
    backgroundColor: 'rgba(6,182,212,0.1)',
    color: '#67e8f9',
  },
  titleInput: {
    width: '100%',
    maxWidth: '360px',
    padding: '8px 12px',
    borderRadius: '6px',
    border: '1px solid var(--color-border)',
    backgroundColor: 'var(--color-surface)',
    color: 'var(--color-text)',
    fontSize: '13px',
    marginBottom: '12px',
    textAlign: 'center',
    outline: 'none',
  },
  progress: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '12px',
    color: '#67e8f9',
    fontSize: '13px',
  },
  progressBar: {
    width: '100%',
    maxWidth: '240px',
    height: '4px',
    borderRadius: '2px',
    backgroundColor: 'rgba(6,182,212,0.15)',
    overflow: 'hidden',
  },
  progressFill: {
    height: '100%',
    borderRadius: '2px',
    backgroundColor: '#06b6d4' as const,
    transition: 'width 0.3s ease',
  },
  success: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '12px',
    color: '#22c55e' as const,
    fontSize: '13px',
    fontWeight: '500',
  },
  error: {
    color: '#ef4444',
    fontSize: '13px',
    marginTop: '8px',
  },
  hiddenInput: {
    display: 'none',
  },
});

export default function DocumentUpload({ onUploadComplete, provider }: DocumentUploadProps) {
  const styles = useStyles();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [dragOver, setDragOver] = useState(false);
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [title, setTitle] = useState('');
  const [uploading, setUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState(0);
  const [uploadResult, setUploadResult] = useState<{
    success: boolean;
    error?: string;
    safetyDisplay?: SafetyBlockDisplayModel;
    quota?: {
      reason: string;
      quotas: KnowledgeQuotas | null;
      usage: KnowledgeUsage | null;
    };
    accepted?: {
      chunkCount: number;
      source: string;
    };
  } | null>(null);

  const isReadOnly = provider ? !provider.supportsMutation : false;
  const isVolatile = provider ? !provider.persistent : false;

  const handleFileSelect = useCallback((file: File) => {
    const ext = '.' + file.name.split('.').pop()?.toLowerCase();
    if (!ACCEPTED_FORMATS.includes(ext)) {
      setUploadResult({ success: false, error: `Unsupported format. Accepted: ${ACCEPTED_FORMATS.join(', ')}` });
      return;
    }
    setSelectedFile(file);
    setTitle(file.name.replace(/\.[^/.]+$/, ''));
    setUploadResult(null);
  }, []);

  const handleDrop = useCallback((e: React.DragEvent) => {
    if (isReadOnly) { e.preventDefault(); return; }
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files[0];
    if (file) handleFileSelect(file);
  }, [handleFileSelect, isReadOnly]);

  const handleUpload = useCallback(async () => {
    if (!selectedFile || !title.trim()) return;
    setUploading(true);
    setUploadProgress(0);
    setUploadResult(null);

    // Simulate progress (real implementation would use XHR with progress events)
    const progressInterval = setInterval(() => {
      setUploadProgress(prev => Math.min(prev + 15, 85));
    }, 200);

    try {
      const response = await uploadDocument(selectedFile, title);
      clearInterval(progressInterval);
      setUploadProgress(100);
      setUploadResult({
        success: true,
        accepted: {
          chunkCount: response.chunkCount ?? 0,
          source: response.source ?? 'upload',
        },
      });
      onUploadComplete();
      setTimeout(() => {
        setSelectedFile(null);
        setTitle('');
        setUploadProgress(0);
        setUploadResult(null);
      }, 3000);
    } catch (e) {
      clearInterval(progressInterval);
      if (e instanceof KnowledgeUploadError) {
        setUploadResult({ success: false, safetyDisplay: e.display });
      } else if (e instanceof KnowledgeQuotaError) {
        setUploadResult({
          success: false,
          quota: { reason: e.message, quotas: e.quotas, usage: e.usage },
        });
      } else if (e instanceof KnowledgeMutationUnsupportedError) {
        setUploadResult({ success: false, error: e.message });
      } else {
        setUploadResult({ success: false, error: e instanceof Error ? e.message : 'Upload failed' });
      }
    } finally {
      setUploading(false);
    }
  }, [selectedFile, title, onUploadComplete]);

  if (isReadOnly) {
    return (
      <div
        className={`${styles.wrapper} ${styles.wrapperReadOnly}`}
        data-testid="document-upload"
        data-upload-mode="read-only"
      >
        <div className={styles.readOnlyBanner} role="status" data-testid="upload-readonly">
          🔒 The active knowledge provider is read-only — its corpus is managed outside
          Retail Pulse. Manage documents in the provider’s portal and re-run search.
        </div>
      </div>
    );
  }

  return (
    <div
      className={`${styles.wrapper} ${dragOver ? styles.wrapperDragOver : ''}`}
      style={dragOver ? { borderColor: '#06b6d4', backgroundColor: 'rgba(6,182,212,0.1)' } : undefined}
      data-testid="document-upload"
      data-upload-mode={isVolatile ? 'volatile' : 'durable'}
      onDragOver={e => { e.preventDefault(); setDragOver(true); }}
      onDragLeave={() => setDragOver(false)}
      onDrop={handleDrop}
      onClick={() => !selectedFile && fileInputRef.current?.click()}
    >
      {isVolatile && (
        <div
          className={styles.volatileWarning}
          role="alert"
          data-testid="upload-volatile-warning"
        >
          <span aria-hidden="true">⚠️</span>
          <span>
            The active provider is <strong>volatile</strong>. Uploaded content lives
            only in this process and will be lost on restart. Use a durable provider
            for content you need to keep.
          </span>
        </div>
      )}

      <input
        ref={fileInputRef}
        type="file"
        className={styles.hiddenInput}
        accept={ACCEPTED_FORMATS.join(',')}
        onChange={e => {
          const file = e.target.files?.[0];
          if (file) handleFileSelect(file);
        }}
        data-testid="file-input"
      />

      {!selectedFile && !uploading && !uploadResult?.success && (
        <>
          <div className={styles.icon}>📤</div>
          <div className={styles.label}>Drop files here or click to browse</div>
          <div className={styles.hint}>Upload documents to enrich the knowledge base</div>
          <div className={styles.formats}>
            {ACCEPTED_FORMATS.map(f => (
              <span key={f} className={styles.formatBadge}>{f}</span>
            ))}
          </div>
        </>
      )}

      {selectedFile && !uploading && !uploadResult && (
        <>
          <div className={styles.icon}>📎</div>
          <div className={styles.label}>{selectedFile.name}</div>
          <input
            className={styles.titleInput}
            placeholder="Document title..."
            value={title}
            onChange={e => setTitle(e.target.value)}
            onClick={e => e.stopPropagation()}
            data-testid="title-input"
          />
          <Button
            appearance="primary"
            onClick={e => { e.stopPropagation(); handleUpload(); }}
            disabled={!title.trim()}
            style={{ backgroundColor: KB_COLORS.primary }}
            data-testid="upload-btn"
          >
            Upload & Index
          </Button>
        </>
      )}

      {uploading && (
        <div className={styles.progress} data-testid="upload-progress">
          <span>⏳ Indexing document...</span>
          <div className={styles.progressBar}>
            <div className={styles.progressFill} style={{ width: `${uploadProgress}%` }} />
          </div>
          <span>{uploadProgress}%</span>
        </div>
      )}

      {uploadResult?.success && (
        <div className={styles.success} data-testid="upload-success">
          ✅ Document indexed successfully
          {uploadResult.accepted && (
            <div className={styles.outcomeMeta} data-testid="upload-accepted-meta">
              {uploadResult.accepted.chunkCount} chunk
              {uploadResult.accepted.chunkCount === 1 ? '' : 's'}{' '}
              stored as source “{uploadResult.accepted.source}”.
            </div>
          )}
        </div>
      )}

      {uploadResult?.safetyDisplay && (
        <div data-testid="upload-safety-block" style={{ marginTop: '12px' }}>
          <KnowledgeIngestionBlock
            documentTitle={title || selectedFile?.name}
            display={uploadResult.safetyDisplay}
          />
        </div>
      )}

      {uploadResult?.quota && (
        <div className={styles.quotaBlock} role="alert" data-testid="upload-quota-block">
          <div>🚫 Quota reached — {uploadResult.quota.reason}</div>
          {uploadResult.quota.quotas && uploadResult.quota.usage && (
            <div className={styles.outcomeMeta} data-testid="upload-quota-meta">
              Documents {uploadResult.quota.usage.documentCount.toLocaleString()} / {uploadResult.quota.quotas.maxDocuments.toLocaleString()},
              chunks {uploadResult.quota.usage.chunkCount.toLocaleString()} / {uploadResult.quota.quotas.maxChunks.toLocaleString()}.
            </div>
          )}
        </div>
      )}

      {uploadResult?.error && (
        <div className={styles.error} data-testid="upload-error">
          ❌ {uploadResult.error}
        </div>
      )}
    </div>
  );
}
