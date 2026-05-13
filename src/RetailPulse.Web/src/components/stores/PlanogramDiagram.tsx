import { makeStyles } from '@fluentui/react-components';
import { STORE_COLORS } from '../../constants/agentRouting';
import type { PlanogramLayout } from '../../types';

interface PlanogramDiagramProps {
  before: PlanogramLayout;
  after: PlanogramLayout;
  comparisonMode?: boolean;
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },
  titleRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
  },
  title: {
    fontSize: '15px',
    fontWeight: '600',
    color: STORE_COLORS.eyeLevelBorder,
  },
  comparison: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '20px',
  },
  single: {
    display: 'flex',
    flexDirection: 'column',
  },
  panelLabel: {
    fontSize: '12px',
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: '1px',
    marginBottom: '10px',
    padding: '4px 10px',
    borderRadius: '4px',
    display: 'inline-block',
    alignSelf: 'flex-start',
  },
  shelfContainer: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    background: STORE_COLORS.cardBg,
    border: `1px solid ${STORE_COLORS.cardBorder}`,
    borderRadius: '10px',
    padding: '12px',
  },
  shelfRow: {
    display: 'flex',
    alignItems: 'stretch',
    minHeight: '52px',
    borderRadius: '6px',
    overflow: 'hidden',
    border: `1px solid ${STORE_COLORS.shelfBorder}`,
    background: STORE_COLORS.shelfBg,
    transition: 'all 0.2s ease',
  },
  eyeLevelShelf: {
    borderColor: STORE_COLORS.eyeLevelBorder as unknown as undefined,
    background: STORE_COLORS.eyeLevel,
    boxShadow: `inset 0 0 0 1px ${STORE_COLORS.eyeLevelBorder}`,
  },
  shelfLabel: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '72px',
    minWidth: '72px',
    fontSize: '11px',
    fontWeight: '600',
    color: 'var(--color-text-muted, rgba(255,255,255,0.55))',
    borderRight: `1px solid ${STORE_COLORS.shelfBorder}`,
    padding: '4px',
    flexShrink: 0,
  },
  eyeLevelLabel: {
    fontSize: '10px',
    color: STORE_COLORS.eyeLevelBorder,
    fontWeight: '700',
  },
  slotsRow: {
    display: 'flex',
    flex: 1,
    overflow: 'hidden',
  },
  slot: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '6px 4px',
    borderRight: `1px solid ${STORE_COLORS.shelfBorder}`,
    position: 'relative',
    transition: 'all 0.2s ease',
    ':hover': {
      background: STORE_COLORS.heatmapHover,
    },
  },
  slotName: {
    fontSize: '10px',
    fontWeight: '600',
    color: 'var(--color-text, #e2e8f0)',
    textAlign: 'center',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    maxWidth: '100%',
  },
  slotBrand: {
    fontSize: '9px',
    color: 'var(--color-text-muted, rgba(255,255,255,0.45))',
    marginTop: '2px',
  },
  upliftBadge: {
    position: 'absolute',
    top: '2px',
    right: '2px',
    fontSize: '9px',
    fontWeight: '700',
    color: STORE_COLORS.uplift,
    background: 'rgba(34,197,94,0.18)',
    padding: '1px 4px',
    borderRadius: '3px',
    lineHeight: '1.3',
  },
  empty: {
    padding: '40px',
    textAlign: 'center',
    color: 'var(--color-text-muted, rgba(255,255,255,0.5))',
    fontSize: '14px',
  },
});

function ShelfView({
  layout,
  styles,
}: {
  layout: PlanogramLayout;
  styles: ReturnType<typeof useStyles>;
}) {
  const shelves = Array.from({ length: layout.shelfCount }, (_, i) => i + 1);

  return (
    <div className={styles.shelfContainer}>
      {shelves.map(level => {
        const isEyeLevel = layout.eyeLevelShelves.includes(level);
        const slots = layout.slots
          .filter(s => s.shelfLevel === level)
          .sort((a, b) => a.position - b.position);

        return (
          <div
            key={level}
            className={`${styles.shelfRow} ${isEyeLevel ? styles.eyeLevelShelf : ''}`}
            data-testid={`shelf-row-${level}`}
          >
            <div className={styles.shelfLabel}>
              <div>
                <div>Shelf {level}</div>
                {isEyeLevel && (
                  <div className={styles.eyeLevelLabel}>👁 Eye Level</div>
                )}
              </div>
            </div>
            <div className={styles.slotsRow}>
              {slots.map((slot, idx) => (
                <div
                  key={`${slot.skuName}-${idx}`}
                  className={styles.slot}
                  style={{
                    flex: slot.facingWidth,
                    background: `${slot.brandColor}20`,
                    borderLeft: `3px solid ${slot.brandColor}`,
                  }}
                  data-testid="planogram-slot"
                  title={`${slot.skuName} (${slot.brand})`}
                >
                  <div className={styles.slotName}>{slot.skuName}</div>
                  <div className={styles.slotBrand}>{slot.brand}</div>
                  {slot.predictedUplift != null && slot.predictedUplift > 0 && (
                    <span className={styles.upliftBadge} data-testid="uplift-badge">
                      +{slot.predictedUplift}%
                    </span>
                  )}
                </div>
              ))}
              {slots.length === 0 && (
                <div
                  style={{
                    flex: 1,
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    fontSize: '11px',
                    color: 'rgba(255,255,255,0.3)',
                  }}
                >
                  Empty shelf
                </div>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}

export function PlanogramDiagram({ before, after, comparisonMode }: PlanogramDiagramProps) {
  const styles = useStyles();

  const isEmpty = before.shelfCount === 0 && after.shelfCount === 0;

  if (isEmpty) {
    return (
      <div data-testid="planogram-diagram">
        <div className={styles.titleRow}>
          <span className={styles.title}>📦 Planogram Layout</span>
        </div>
        <div className={styles.empty} data-testid="planogram-empty">No planogram data available</div>
      </div>
    );
  }

  if (comparisonMode) {
    return (
      <div data-testid="planogram-diagram">
        <div className={styles.titleRow}>
          <span className={styles.title}>📦 Planogram Comparison</span>
        </div>
        <div className={styles.comparison}>
          <div className={styles.single}>
            <span
              className={styles.panelLabel}
              style={{ background: 'rgba(239,68,68,0.15)', color: STORE_COLORS.red }}
            >
              Before
            </span>
            <ShelfView layout={before} styles={styles} />
          </div>
          <div className={styles.single}>
            <span
              className={styles.panelLabel}
              style={{ background: 'rgba(34,197,94,0.15)', color: STORE_COLORS.green }}
            >
              After
            </span>
            <ShelfView layout={after} styles={styles} />
          </div>
        </div>
      </div>
    );
  }

  return (
    <div data-testid="planogram-diagram">
      <div className={styles.titleRow}>
        <span className={styles.title}>📦 Planogram Layout</span>
      </div>
      <ShelfView layout={after} styles={styles} />
    </div>
  );
}
