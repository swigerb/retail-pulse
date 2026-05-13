import { useState, useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import { CARD_COLORS } from '../../constants/agentRouting';
import type { AdaptiveCard, DrillDownLevel } from '../../types';
import CardLifecycleIndicator from './CardLifecycleIndicator';

interface DrillDownCardProps {
  card: AdaptiveCard;
  levels: DrillDownLevel[];
}

const useStyles = makeStyles({
  card: {
    background: CARD_COLORS.cardBg,
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    borderRadius: '12px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
    transition: 'all 0.3s ease',
  },
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  title: {
    fontSize: '17px',
    fontWeight: '700',
    color: 'var(--color-text)',
    lineHeight: '1.3',
  },
  summary: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    lineHeight: '1.5',
  },
  breadcrumb: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    flexWrap: 'wrap',
  },
  breadcrumbItem: {
    cursor: 'pointer',
    color: CARD_COLORS.active,
    ':hover': {
      textDecoration: 'underline',
    },
  },
  breadcrumbSep: {
    color: 'rgba(255,255,255,0.2)',
    userSelect: 'none',
  },
  breadcrumbCurrent: {
    color: 'var(--color-text)',
    fontWeight: '600',
  },
  backBtn: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    padding: '4px 12px',
    borderRadius: '6px',
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    background: 'rgba(255,255,255,0.04)',
    color: 'var(--color-text-muted)',
    fontSize: '12px',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    alignSelf: 'flex-start',
    ':hover': {
      background: 'rgba(255,255,255,0.08)',
      color: 'var(--color-text)',
    },
  },
  itemsList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  row: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: '10px 14px',
    borderRadius: '8px',
    background: 'rgba(255,255,255,0.03)',
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.06)',
    },
  },
  rowName:{
    fontSize: '14px',
    color: 'var(--color-text)',
    fontWeight: '500',
  },
  rowValue: {
    fontSize: '14px',
    fontWeight: '700',
    color: CARD_COLORS.active,
  },
  rowExpander: {
    fontSize: '12px',
    color: 'var(--color-text-muted)',
    marginLeft: '8px',
    transition: 'transform 0.2s ease',
  },
  subItems: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    overflow: 'hidden',
    transition: 'max-height 0.35s ease, opacity 0.3s ease',
  },
  subRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: '6px 14px 6px 28px',
    borderRadius: '6px',
    background: 'rgba(255,255,255,0.02)',
    fontSize: '13px',
  },
  subName: {
    color: 'var(--color-text-muted)',
  },
  subValue: {
    fontWeight: '600',
    color: 'var(--color-text)',
  },
  emptyState: {
    fontSize: '13px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
    padding: '20px 0',
  },
});

export default function DrillDownCard({ card, levels }: DrillDownCardProps) {
  const styles = useStyles();
  const [levelIndex, setLevelIndex] = useState(0);
  const [expandedItem, setExpandedItem] = useState<string | null>(null);
  const [breadcrumbPath, setBreadcrumbPath] = useState<string[]>([]);

  const currentLevel = useMemo(() => levels[levelIndex] ?? null, [levels, levelIndex]);

  const handleItemClick = (itemName: string, hasSubItems: boolean) => {
    if (hasSubItems) {
      if (expandedItem === itemName) {
        setExpandedItem(null);
      } else {
        setExpandedItem(itemName);
      }
    }
    // If there's a deeper level, drill into it
    if (levelIndex + 1 < levels.length && !hasSubItems) {
      setBreadcrumbPath((prev) => [...prev, currentLevel?.label ?? '']);
      setExpandedItem(null);
      setLevelIndex((prev) => prev + 1);
    }
  };

  const handleBack = () => {
    if (levelIndex > 0) {
      setBreadcrumbPath((prev) => prev.slice(0, -1));
      setExpandedItem(null);
      setLevelIndex((prev) => prev - 1);
    }
  };

  const handleBreadcrumbClick = (targetIndex: number) => {
    setBreadcrumbPath((prev) => prev.slice(0, targetIndex));
    setExpandedItem(null);
    setLevelIndex(targetIndex);
  };

  if (!currentLevel) {
    return (
      <div className={styles.card} data-testid="drilldown-card">
        <span className={styles.emptyState}>No drill-down data available</span>
      </div>
    );
  }

  return (
    <div className={styles.card} data-testid="drilldown-card">
      <div className={styles.header}>
        <span className={styles.title}>{card.title}</span>
        <span className={styles.summary}>{card.summary}</span>
      </div>

      <CardLifecycleIndicator currentState={card.state} stateChangedAt={card.stateChangedAt} />

      {/* Breadcrumb */}
      {breadcrumbPath.length > 0 && (
        <div className={styles.breadcrumb} data-testid="drilldown-breadcrumb">
          {breadcrumbPath.map((label, i) => (
            <span key={i}>
              <span className={styles.breadcrumbItem} onClick={() => handleBreadcrumbClick(i)}>
                {label}
              </span>
              <span className={styles.breadcrumbSep}> › </span>
            </span>
          ))}
          <span className={styles.breadcrumbCurrent}>{currentLevel.label}</span>
        </div>
      )}

      {/* Back button */}
      {levelIndex > 0 && (
        <button className={styles.backBtn} onClick={handleBack} data-testid="drilldown-back">
          ← Back
        </button>
      )}

      {/* Items list */}
      <div className={styles.itemsList}>
        {currentLevel.data.map((item) => {
          const hasSubItems = (item.subItems?.length ?? 0) > 0;
          const isExpanded = expandedItem === item.name;

          return (
            <div key={item.name}>
              <div
                className={styles.row}
                onClick={() => handleItemClick(item.name, hasSubItems)}
                data-testid="drilldown-row"
              >
                <span className={styles.rowName}>{item.name}</span>
                <span>
                  <span className={styles.rowValue}>{item.value.toLocaleString()}</span>
                  {hasSubItems && (
                    <span
                      className={styles.rowExpander}
                      style={{ display: 'inline-block', transform: isExpanded ? 'rotate(90deg)' : 'rotate(0)' }}
                    >
                      ▶
                    </span>
                  )}
                </span>
              </div>

              {/* Sub-items with animated expand */}
              {hasSubItems && (
                <div
                  className={styles.subItems}
                  style={{
                    maxHeight: isExpanded ? `${(item.subItems?.length ?? 0) * 40}px` : '0px',
                    opacity: isExpanded ? 1 : 0,
                  }}
                >
                  {item.subItems?.map((sub) => (
                    <div key={sub.name} className={styles.subRow}>
                      <span className={styles.subName}>{sub.name}</span>
                      <span className={styles.subValue}>{sub.value.toLocaleString()}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
