import { useState, useCallback, useRef } from 'react';
import { makeStyles, Button } from '@fluentui/react-components';
import { KB_COLORS } from '../../constants/agentRouting';
import { uploadDocument, KnowledgeUploadError } from '../../services/knowledgeApi';
import type { SafetyBlockDisplayModel } from '../../types';
import { KnowledgeIngestionBlock } from '../guardrails/KnowledgeIngestionBlock';

interface DocumentUploadProps {
  onUploadComplete: () => void;
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

export default function DocumentUpload({ onUploadComplete }: DocumentUploadProps) {
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
  } | null>(null);

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
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files[0];
    if (file) handleFileSelect(file);
  }, [handleFileSelect]);

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
      await uploadDocument(selectedFile, title);
      clearInterval(progressInterval);
      setUploadProgress(100);
      setUploadResult({ success: true });
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
      } else {
        setUploadResult({ success: false, error: e instanceof Error ? e.message : 'Upload failed' });
      }
    } finally {
      setUploading(false);
    }
  }, [selectedFile, title, onUploadComplete]);

  return (
    <div
      className={`${styles.wrapper} ${dragOver ? styles.wrapperDragOver : ''}`}
      style={dragOver ? { borderColor: '#06b6d4', backgroundColor: 'rgba(6,182,212,0.1)' } : undefined}
      data-testid="document-upload"
      onDragOver={e => { e.preventDefault(); setDragOver(true); }}
      onDragLeave={() => setDragOver(false)}
      onDrop={handleDrop}
      onClick={() => !selectedFile && fileInputRef.current?.click()}
    >
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

      {uploadResult?.error && (
        <div className={styles.error} data-testid="upload-error">
          ❌ {uploadResult.error}
        </div>
      )}
    </div>
  );
}
