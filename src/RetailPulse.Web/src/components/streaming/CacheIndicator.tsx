import { useState } from 'react';
import { makeStyles, Tooltip } from '@fluentui/react-components';
import type { CacheInfo } from '../../types';

export interface CacheIndicatorProps {
  cacheInfo: CacheInfo;
}

const useStyles = makeStyles({
  badge: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 10px',
    borderRadius: '16px',
    fontSize: '12px',
    fontWeight: '500',
    backgroundColor: 'rgba(250, 204, 21, 0.12)',
    color: '#facc15',
    border: '1px solid rgba(250, 204, 21, 0.25)',
    cursor: 'default',
    animationName: {
      '0%': { opacity: 0, transform: 'scale(0.9)' },
      '100%': { opacity: 1, transform: 'scale(1)' },
    },
    animationDuration: '300ms',
    animationTimingFunction: 'ease-out',
    animationFillMode: 'forwards',
  },
  bolt: {
    display: 'inline-block',
    animationName: {
      '0%': { transform: 'scale(1)' },
      '25%': { transform: 'scale(1.3)' },
      '50%': { transform: 'scale(1)' },
      '75%': { transform: 'scale(1.15)' },
      '100%': { transform: 'scale(1)' },
    },
    animationDuration: '600ms',
    animationTimingFunction: 'ease-in-out',
  },
  saved: {
    fontSize: '11px',
    color: 'rgba(250, 204, 21, 0.7)',
  },
});

export function CacheIndicator({ cacheInfo }: CacheIndicatorProps) {
  const styles = useStyles();
  const [showTooltip, setShowTooltip] = useState(false);

  if (!cacheInfo.cached) return null;

  const timeSavedDisplay = cacheInfo.timeSavedMs
    ? `Saved ~${(cacheInfo.timeSavedMs / 1000).toFixed(1)}s`
    : null;

  const ttlDisplay = cacheInfo.ttlSeconds
    ? `Ask again after ${cacheInfo.ttlSeconds}s for a fresh answer.`
    : '';

  const tooltipContent = `This response was served from cache. ${ttlDisplay}`;

  return (
    <Tooltip
      content={tooltipContent}
      relationship="description"
      visible={showTooltip}
      onVisibleChange={(_e, data) => setShowTooltip(data.visible)}
    >
      <span
        className={styles.badge}
        data-testid="cache-indicator"
        onMouseEnter={() => setShowTooltip(true)}
        onMouseLeave={() => setShowTooltip(false)}
      >
        <span className={styles.bolt}>⚡</span>
        <span>Cached</span>
        {timeSavedDisplay && <span className={styles.saved}>{timeSavedDisplay}</span>}
      </span>
    </Tooltip>
  );
}
