import {
  Dialog,
  DialogSurface,
  DialogBody,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  makeStyles,
} from '@fluentui/react-components';

/**
 * Replacement for the "hung spinner" behavior when a chat request exceeds
 * the frontend timeout (issue #92). Users get a clear choice: retry the
 * same prompt (creates a fresh request) or abandon the run entirely so
 * the UI returns to an editable state without a stale in-flight banner.
 */
export interface TimeoutDialogProps {
  readonly open: boolean;
  readonly timeoutMs: number;
  readonly onRetry: () => void;
  readonly onAbandon: () => void;
}

const useStyles = makeStyles({
  bodyText: {
    fontSize: '14px',
    lineHeight: '1.5',
    color: 'var(--color-text)',
  },
  hint: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    marginTop: '8px',
  },
});

export function TimeoutDialog({ open, timeoutMs, onRetry, onAbandon }: TimeoutDialogProps) {
  const styles = useStyles();
  const seconds = Math.max(1, Math.round(timeoutMs / 1000));

  return (
    <Dialog
      open={open}
      onOpenChange={(_e, data) => {
        if (!data.open) onAbandon();
      }}
      modalType="alert"
    >
      <DialogSurface data-testid="timeout-dialog">
        <DialogBody>
          <DialogTitle>This is taking longer than expected</DialogTitle>
          <DialogContent>
            <div className={styles.bodyText}>
              We didn&apos;t hear back from Retail Pulse within {seconds}&nbsp;seconds. The
              server may still be working, but the browser has stopped waiting so you
              can decide what to do next.
            </div>
            <div className={styles.hint}>
              <strong>Retry</strong> sends the same question again. <strong>Abandon</strong> clears
              the in-flight indicator and lets you edit or try a different prompt.
            </div>
          </DialogContent>
          <DialogActions>
            <Button
              appearance="secondary"
              onClick={onAbandon}
              data-testid="timeout-dialog-abandon"
            >
              Abandon
            </Button>
            <Button
              appearance="primary"
              onClick={onRetry}
              data-testid="timeout-dialog-retry"
            >
              Retry
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
