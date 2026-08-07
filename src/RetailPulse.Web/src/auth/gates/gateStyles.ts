import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * Shared visual shell for every provider sign-in gate (Entra / GitHub / Anonymous) so the
 * unauthenticated experience is pixel-identical regardless of the configured provider. Only the
 * headings, copy and action buttons differ per provider; the card chrome does not.
 */
export const gateStyles = makeStyles({
  root: {
    position: 'fixed',
    inset: 0,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background:
      'radial-gradient(1200px 800px at 50% -10%, rgba(91,124,255,0.18), transparent), #0b0e14',
    padding: '24px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '20px',
    maxWidth: '420px',
    width: '100%',
    padding: '40px 36px',
    borderRadius: tokens.borderRadiusXLarge,
    background: tokens.colorNeutralBackground2,
    boxShadow: tokens.shadow28,
    textAlign: 'center',
  },
  headings: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    width: '100%',
  },
});
