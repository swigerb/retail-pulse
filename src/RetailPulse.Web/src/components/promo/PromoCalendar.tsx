import { useMemo, useState, useRef, useCallback, useEffect } from 'react';
import { makeStyles } from '@fluentui/react-components';
import type { PromoCampaign } from '../../types';
import { PROMO_COLORS } from '../../constants/agentRouting';

interface PromoCalendarProps {
  campaigns: PromoCampaign[];
  proposedCampaign?: Omit<PromoCampaign, 'id' | 'status'> & { status?: 'proposed' };
}

const STATUS_COLORS: Record<string, string> = {
  active: PROMO_COLORS.calendarActive,
  completed: PROMO_COLORS.calendarCompleted,
  planned: PROMO_COLORS.calendarPlanned,
  proposed: PROMO_COLORS.calendarProposed,
};

const WEEK_MS = 7 * 24 * 60 * 60 * 1000;
const WEEK_PX = 80;

const useStyles = makeStyles({
  wrapper: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '20px',
    borderRadius: '12px',
    backgroundColor: 'var(--color-surface-alt, rgba(255,255,255,0.02))',
    border: '1px solid rgba(255,255,255,0.06)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: '#22c55e',
  },
  legend: {
    display: 'flex',
    gap: '12px',
    flexWrap: 'wrap',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '11px',
    color: '#94a3b8',
  },
  legendDot: {
    width: '8px',
    height: '8px',
    borderRadius: '2px',
  },
  scrollContainer: {
    overflowX: 'auto',
    position: 'relative',
    paddingBottom: '8px',
  },
  timeline: {
    position: 'relative',
    minHeight: '100px',
  },
  weekHeaders: {
    display: 'flex',
    borderBottom: '1px solid rgba(255,255,255,0.06)',
    marginBottom: '8px',
  },
  weekHeader: {
    fontSize: '10px',
    color: '#64748b',
    textAlign: 'center',
    flexShrink: 0,
    padding: '4px 0',
    borderRight: '1px solid rgba(255,255,255,0.03)',
  },
  regionGroup: {
    marginBottom: '16px',
  },
  regionLabel: {
    fontSize: '11px',
    fontWeight: '600',
    color: '#94a3b8',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '6px',
  },
  row: {
    position: 'relative',
    height: '32px',
    marginBottom: '4px',
  },
  bar: {
    position: 'absolute',
    height: '26px',
    borderRadius: '4px',
    display: 'flex',
    alignItems: 'center',
    paddingLeft: '8px',
    fontSize: '11px',
    fontWeight: '500',
    color: '#fff',
    cursor: 'default',
    transition: 'opacity 0.15s',
    overflow: 'hidden',
    whiteSpace: 'nowrap',
    textOverflow: 'ellipsis',
    ':hover': {
      opacity: '0.9',
    },
  },
  tooltip: {
    position: 'absolute',
    zIndex: 100,
    padding: '10px 14px',
    borderRadius: '8px',
    backgroundColor: '#1e1b2e',
    border: '1px solid rgba(139,92,246,0.3)',
    color: '#f1f5f9',
    fontSize: '12px',
    lineHeight: '1.5',
    pointerEvents: 'none',
    minWidth: '160px',
    boxShadow: '0 4px 12px rgba(0,0,0,0.5)',
  },
  conflictIndicator: {
    position: 'absolute',
    top: '0',
    width: '2px',
    height: '100%',
    backgroundColor: PROMO_COLORS.calendarConflict,
    opacity: 0.6,
  },
  empty: {
    fontSize: '13px',
    color: '#64748b',
    fontStyle: 'italic',
    textAlign: 'center',
    padding: '24px',
  },
});

function getWeekLabel(date: Date): string {
  return date.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}

function detectOverlaps(campaigns: Array<PromoCampaign & { isProposed?: boolean }>): Set<string> {
  const conflicts = new Set<string>();
  for (let i = 0; i < campaigns.length; i++) {
    for (let j = i + 1; j < campaigns.length; j++) {
      const a = campaigns[i], b = campaigns[j];
      if (a.region !== b.region) continue;
      const aStart = new Date(a.startDate).getTime();
      const aEnd = new Date(a.endDate).getTime();
      const bStart = new Date(b.startDate).getTime();
      const bEnd = new Date(b.endDate).getTime();
      if (aStart <= bEnd && bStart <= aEnd) {
        conflicts.add(a.id);
        conflicts.add(b.id);
      }
    }
  }
  return conflicts;
}

export default function PromoCalendar({ campaigns, proposedCampaign }: PromoCalendarProps) {
  const styles = useStyles();
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [tooltipPos, setTooltipPos] = useState<{ x: number; y: number }>({ x: 0, y: 0 });
  const scrollRef = useRef<HTMLDivElement>(null);

  const allCampaigns = useMemo(() => {
    const items: Array<PromoCampaign & { isProposed?: boolean }> = [...campaigns];
    if (proposedCampaign) {
      items.push({
        ...proposedCampaign,
        id: '__proposed__',
        status: 'proposed',
        isProposed: true,
      } as PromoCampaign & { isProposed?: boolean });
    }
    return items;
  }, [campaigns, proposedCampaign]);

  const { timelineStart, weekCount, weeks } = useMemo(() => {
    const now = new Date();
    // 3 months back, 3 months forward = ~26 weeks
    const start = new Date(now.getTime() - 13 * WEEK_MS);
    start.setDate(start.getDate() - start.getDay()); // align to Sunday
    const count = 26;
    const wks: Date[] = [];
    for (let i = 0; i < count; i++) {
      wks.push(new Date(start.getTime() + i * WEEK_MS));
    }
    return { timelineStart: start.getTime(), weekCount: count, weeks: wks };
  }, []);

  const conflicts = useMemo(() => detectOverlaps(allCampaigns), [allCampaigns]);

  const regionGroups = useMemo(() => {
    const groups = new Map<string, Array<PromoCampaign & { isProposed?: boolean }>>();
    for (const c of allCampaigns) {
      const key = c.region;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(c);
    }
    return groups;
  }, [allCampaigns]);

  const getBarPosition = useCallback((startDate: string, endDate: string) => {
    const start = new Date(startDate).getTime();
    const end = new Date(endDate).getTime();
    const left = ((start - timelineStart) / WEEK_MS) * WEEK_PX;
    const width = Math.max(((end - start) / WEEK_MS) * WEEK_PX, 20);
    return { left, width };
  }, [timelineStart]);

  // Scroll to "now" on mount
  useEffect(() => {
    if (scrollRef.current) {
      const nowOffset = ((Date.now() - timelineStart) / WEEK_MS) * WEEK_PX;
      scrollRef.current.scrollLeft = Math.max(0, nowOffset - scrollRef.current.clientWidth / 2);
    }
  }, [timelineStart]);

  const totalWidth = weekCount * WEEK_PX;
  const hoveredCampaign = allCampaigns.find(c => c.id === hoveredId);

  if (allCampaigns.length === 0) {
    return (
      <div className={styles.wrapper} data-testid="promo-calendar">
        <div className={styles.header}>
          <span className={styles.title}>📅 Campaign Calendar</span>
        </div>
        <div className={styles.empty}>No campaigns to display</div>
      </div>
    );
  }

  return (
    <div className={styles.wrapper} data-testid="promo-calendar">
      <div className={styles.header}>
        <span className={styles.title}>📅 Campaign Calendar</span>
        <div className={styles.legend}>
          {Object.entries(STATUS_COLORS).map(([status, color]) => (
            <span key={status} className={styles.legendItem}>
              <span className={styles.legendDot} style={{ backgroundColor: color }} />
              {status.charAt(0).toUpperCase() + status.slice(1)}
            </span>
          ))}
        </div>
      </div>

      <div className={styles.scrollContainer} ref={scrollRef}>
        <div className={styles.timeline} style={{ width: totalWidth }}>
          <div className={styles.weekHeaders}>
            {weeks.map((w, i) => (
              <div key={i} className={styles.weekHeader} style={{ width: WEEK_PX }}>
                {getWeekLabel(w)}
              </div>
            ))}
          </div>

          {Array.from(regionGroups.entries()).map(([region, regionCampaigns]) => (
            <div key={region} className={styles.regionGroup}>
              <div className={styles.regionLabel}>{region}</div>
              {regionCampaigns.map(campaign => {
                const { left, width } = getBarPosition(campaign.startDate, campaign.endDate);
                const isConflict = conflicts.has(campaign.id);
                const isProposed = (campaign as PromoCampaign & { isProposed?: boolean }).isProposed;
                const color = isConflict ? PROMO_COLORS.calendarConflict : STATUS_COLORS[campaign.status] ?? '#6b7280';

                return (
                  <div key={campaign.id} className={styles.row}>
                    <div
                      className={styles.bar}
                      style={{
                        left,
                        width,
                        backgroundColor: `${color}cc`,
                        border: isProposed ? `2px dashed ${color}` : `1px solid ${color}`,
                        backgroundImage: isProposed ? 'repeating-linear-gradient(45deg, transparent, transparent 5px, rgba(255,255,255,0.05) 5px, rgba(255,255,255,0.05) 10px)' : undefined,
                      }}
                      onMouseEnter={e => {
                        setHoveredId(campaign.id);
                        setTooltipPos({ x: e.clientX, y: e.clientY - 60 });
                      }}
                      onMouseLeave={() => setHoveredId(null)}
                      data-testid={`campaign-bar-${campaign.id}`}
                    >
                      {width > 60 ? `${campaign.brand} — ${campaign.name}` : campaign.brand}
                    </div>
                  </div>
                );
              })}
            </div>
          ))}
        </div>

        {hoveredCampaign && (
          <div
            className={styles.tooltip}
            style={{ left: tooltipPos.x + 10, top: tooltipPos.y }}
            data-testid="calendar-tooltip"
          >
            <div style={{ fontWeight: '600', marginBottom: '4px' }}>{hoveredCampaign.name}</div>
            <div>Brand: {hoveredCampaign.brand}</div>
            <div>Budget: ${hoveredCampaign.budget.toLocaleString()}</div>
            <div>Type: {hoveredCampaign.promoType}</div>
            {hoveredCampaign.roi !== undefined && <div>ROI: {hoveredCampaign.roi.toFixed(1)}x</div>}
            {conflicts.has(hoveredCampaign.id) && (
              <div style={{ color: '#fca5a5', marginTop: '4px' }}>⚠️ Overlap detected</div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
