import { useState, useEffect } from 'react';
import {
  makeStyles,
  Button,
  Menu,
  MenuTrigger,
  MenuList,
  MenuItem,
  MenuPopover,
} from '@fluentui/react-components';
import { OBSERVABILITY_COLORS } from '../../constants/agentRouting';
import { fetchExportSessions, fetchExportPreview, exportSession } from '../../services/observabilityApi';
import type { ExportSession, ExportPreview } from '../../types';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  tableWrapper: {
    background: OBSERVABILITY_COLORS.cardBg,
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '12px',
    overflow: 'hidden',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    fontSize: '13px',
  },
  tableHead: {
    textAlign: 'left',
    padding: '12px 14px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.8px',
    borderBottom: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    fontWeight: '600',
    background: 'rgba(255,255,255,0.02)',
  },
  tableRow: {
    transition: 'background 0.15s ease',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.03)',
    },
  },
  tableCell: {
    padding: '12px 14px',
    color: 'var(--color-text)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'middle',
  },
  tableCellMuted: {
    padding: '12px 14px',
    color: 'var(--color-text-muted)',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'middle',
  },
  agentPills: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px',
  },
  agentPill: {
    display: 'inline-block',
    fontSize: '10px',
    padding: '2px 8px',
    borderRadius: '10px',
    background: `${OBSERVABILITY_COLORS.primary}20`,
    color: OBSERVABILITY_COLORS.primary,
    fontWeight: '600',
    whiteSpace: 'nowrap',
  },
  actionCell: {
    padding: '12px 14px',
    borderBottom: '1px solid rgba(255,255,255,0.04)',
    verticalAlign: 'middle',
    display: 'flex',
    gap: '6px',
    alignItems: 'center',
  },
  actionBtn: {
    padding: '5px 12px',
    borderRadius: '6px',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    background: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text)',
    fontSize: '12px',
    fontWeight: '600',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  // Modal overlay
  overlay: {
    position: 'fixed',
    top: '0',
    left: '0',
    right: '0',
    bottom: '0',
    background: 'rgba(0,0,0,0.6)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    zIndex: 100,
    backdropFilter: 'blur(4px)',
  },
  modal: {
    background: 'var(--color-bg-elevated)',
    border: `1px solid ${OBSERVABILITY_COLORS.cardBorder}`,
    borderRadius: '16px',
    padding: '24px',
    width: '600px',
    maxWidth: '90vw',
    maxHeight: '80vh',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    boxShadow: '0 16px 48px rgba(0,0,0,0.5)',
  },
  modalHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  modalTitle: {
    fontSize: '16px',
    fontWeight: '700',
    color: 'var(--color-text)',
  },
  modalClose: {
    background: 'none',
    border: 'none',
    color: 'var(--color-text-muted)',
    fontSize: '20px',
    cursor: 'pointer',
    padding: '4px 8px',
    borderRadius: '6px',
    transition: 'background 0.15s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  modalBody: {
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    flex: 1,
  },
  messageBubble: {
    padding: '12px 16px',
    borderRadius: '12px',
    fontSize: '13px',
    lineHeight: '1.6',
    maxWidth: '85%',
    wordBreak: 'break-word',
  },
  userMessage: {
    alignSelf: 'flex-end',
    background: `${OBSERVABILITY_COLORS.primary}20`,
    color: 'var(--color-text)',
    borderBottomRightRadius: '4px',
  },
  assistantMessage: {
    alignSelf: 'flex-start',
    background: 'rgba(255,255,255,0.06)',
    color: 'var(--color-text)',
    borderBottomLeftRadius: '4px',
  },
  messageTimestamp: {
    fontSize: '10px',
    color: 'var(--color-text-muted)',
    marginTop: '4px',
  },
  modalInfo: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
    padding: '8px',
    background: 'rgba(255,255,255,0.03)',
    borderRadius: '8px',
  },
  skeleton: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  skeletonRow: {
    height: '52px',
    borderRadius: '8px',
    background: 'rgba(255,255,255,0.04)',
    animationName: {
      '0%, 100%': { opacity: 0.4 },
      '50%': { opacity: 0.8 },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
  },
  error: {
    padding: '16px',
    borderRadius: '8px',
    backgroundColor: 'rgba(211,47,47,0.1)',
    border: '1px solid rgba(211,47,47,0.3)',
    color: '#fca5a5',
    fontSize: '13px',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '60px 20px',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
    gap: '8px',
  },
});

function formatTime(ts: string): string {
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString(undefined, {
    month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit',
  });
}

export default function ConversationExport() {
  const styles = useStyles();
  const [sessions, setSessions] = useState<ExportSession[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<ExportPreview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    fetchExportSessions(controller.signal)
      .then(result => {
        setSessions(result);
        setError(null);
        setLoading(false);
      })
      .catch(e => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to load sessions');
        setLoading(false);
      });
    return () => { controller.abort(); };
  }, []);

  const handleExport = async (sessionId: string, format: 'markdown' | 'json') => {
    try {
      const blob = await exportSession(sessionId, format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `session-${sessionId}.${format === 'markdown' ? 'md' : 'json'}`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch {
      setError('Export failed. Please try again.');
    }
  };

  const handlePreview = async (sessionId: string) => {
    setPreviewLoading(true);
    try {
      const result = await fetchExportPreview(sessionId);
      setPreview(result);
    } catch {
      setError('Failed to load preview.');
    } finally {
      setPreviewLoading(false);
    }
  };

  const closePreview = () => setPreview(null);

  return (
    <div className={styles.container} data-testid="conversation-export">
      {error && (
        <div className={styles.error} data-testid="export-error">⚠️ {error}</div>
      )}

      {loading && (
        <div className={styles.skeleton} data-testid="export-skeleton">
          {Array.from({ length: 5 }).map((_, i) => (
            <div key={i} className={styles.skeletonRow} style={{ animationDelay: `${i * 0.1}s` }} />
          ))}
        </div>
      )}

      {!loading && sessions.length === 0 && !error && (
        <div className={styles.emptyState}>
          <span style={{ fontSize: '32px' }}>📤</span>
          <span>No exportable sessions found</span>
        </div>
      )}

      {!loading && sessions.length > 0 && (
        <div className={styles.tableWrapper}>
          <table className={styles.table} data-testid="export-table">
            <thead>
              <tr>
                <th className={styles.tableHead}>Start Time</th>
                <th className={styles.tableHead}>Messages</th>
                <th className={styles.tableHead}>Agents</th>
                <th className={styles.tableHead}>Tokens</th>
                <th className={styles.tableHead}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map(session => (
                <tr key={session.sessionId} className={styles.tableRow} data-testid={`export-row-${session.sessionId}`}>
                  <td className={styles.tableCellMuted}>{formatTime(session.startTime)}</td>
                  <td className={styles.tableCell}>{session.messageCount}</td>
                  <td className={styles.tableCell}>
                    <div className={styles.agentPills}>
                      {session.agentsUsed?.map(agent => (
                        <span key={agent} className={styles.agentPill}>{agent}</span>
                      ))}
                    </div>
                  </td>
                  <td className={styles.tableCellMuted}>{(session.totalTokens ?? 0).toLocaleString()}</td>
                  <td className={styles.actionCell}>
                    <Button
                      size="small"
                      appearance="secondary"
                      onClick={() => handlePreview(session.sessionId)}
                      disabled={previewLoading}
                      data-testid={`preview-btn-${session.sessionId}`}
                    >
                      👁️ Preview
                    </Button>
                    <Menu positioning="below-end">
                      <MenuTrigger disableButtonEnhancement>
                        <Button
                          size="small"
                          appearance="secondary"
                          data-testid={`export-btn-${session.sessionId}`}
                          aria-label={`Export session ${session.sessionId}`}
                        >
                          📥 Export ▾
                        </Button>
                      </MenuTrigger>
                      <MenuPopover data-testid={`export-dropdown-${session.sessionId}`}>
                        <MenuList>
                          <MenuItem
                            onClick={() => handleExport(session.sessionId, 'markdown')}
                            data-testid={`export-md-${session.sessionId}`}
                          >
                            📝 Markdown (.md)
                          </MenuItem>
                          <MenuItem
                            onClick={() => handleExport(session.sessionId, 'json')}
                            data-testid={`export-json-${session.sessionId}`}
                          >
                            🗂️ JSON (.json)
                          </MenuItem>
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Preview Modal */}
      {preview && (
        <div
          className={styles.overlay}
          onClick={closePreview}
          data-testid="preview-modal"
          role="dialog"
          aria-modal="true"
          aria-label="Session preview"
        >
          <div className={styles.modal} onClick={e => e.stopPropagation()}>
            <div className={styles.modalHeader}>
              <span className={styles.modalTitle}>
                🔍 Session Preview
              </span>
              <button
                className={styles.modalClose}
                onClick={closePreview}
                data-testid="preview-close"
                aria-label="Close preview"
              >
                ✕
              </button>
            </div>
            <div className={styles.modalBody}>
              {preview.messages.map((msg, i) => (
                <div
                  key={i}
                  className={`${styles.messageBubble} ${msg.role === 'user' ? styles.userMessage : styles.assistantMessage}`}
                >
                  <div>{msg.content}</div>
                  <div className={styles.messageTimestamp}>{formatTime(msg.timestamp)}</div>
                </div>
              ))}
            </div>
            {preview.totalMessages > preview.messages.length && (
              <div className={styles.modalInfo}>
                Showing {preview.messages.length} of {preview.totalMessages} messages
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
