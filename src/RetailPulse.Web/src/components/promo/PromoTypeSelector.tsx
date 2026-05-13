import { useState, useMemo } from 'react';
import { makeStyles } from '@fluentui/react-components';
import type { PromoType } from '../../types';
import { PROMO_TYPE_CONFIG } from '../../constants/agentRouting';

interface PromoTypeSelectorProps {
  value: PromoType | '';
  onChange: (type: PromoType) => void;
  historicalRoi?: Record<PromoType, number>;
}

const useStyles = makeStyles({
  container: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))',
    gap: '10px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '14px',
    borderRadius: '10px',
    backgroundColor: 'rgba(255,255,255,0.03)',
    border: '1px solid rgba(255,255,255,0.08)',
    cursor: 'pointer',
    transition: 'all 0.2s ease',
  },
  emoji: {
    fontSize: '28px',
    lineHeight: '1',
  },
  typeName: {
    fontSize: '14px',
    fontWeight: '600',
    color: '#e2e8f0',
  },
  description: {
    fontSize: '11px',
    color: '#94a3b8',
    lineHeight: '1.4',
  },
  roiTag: {
    fontSize: '11px',
    fontWeight: '600',
    color: '#22c55e',
    marginTop: '2px',
  },
  hint: {
    fontSize: '10px',
    color: '#64748b',
    fontStyle: 'italic',
  },
});

const PROMO_TYPES: PromoType[] = ['Discount', 'BOGO', 'Display', 'Digital', 'Bundle'];

export default function PromoTypeSelector({ value, onChange, historicalRoi }: PromoTypeSelectorProps) {
  const styles = useStyles();
  const [focused, setFocused] = useState<PromoType | null>(null);

  const cards = useMemo(() => PROMO_TYPES.map(type => ({
    type,
    ...PROMO_TYPE_CONFIG[type],
    avgRoi: historicalRoi?.[type],
  })), [historicalRoi]);

  return (
    <div className={styles.container} data-testid="promo-type-selector">
      {cards.map(card => {
        const isSelected = value === card.type;
        const isFocused = focused === card.type;
        return (
          <div
            key={card.type}
            className={styles.card}
            style={{
              borderColor: isSelected ? '#22c55e' : isFocused ? 'rgba(255,255,255,0.2)' : undefined,
              backgroundColor: isSelected ? 'rgba(34,197,94,0.08)' : undefined,
              boxShadow: isSelected ? '0 0 12px rgba(34,197,94,0.15)' : undefined,
            }}
            onClick={() => onChange(card.type)}
            onMouseEnter={() => setFocused(card.type)}
            onMouseLeave={() => setFocused(null)}
            role="radio"
            aria-checked={isSelected}
            tabIndex={0}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onChange(card.type); } }}
            data-testid={`promo-type-${card.type.toLowerCase()}`}
          >
            <span className={styles.emoji}>{card.emoji}</span>
            <span className={styles.typeName}>{card.type}</span>
            <span className={styles.description}>{card.description}</span>
            {card.avgRoi !== undefined && (
              <span className={styles.roiTag}>Avg ROI: {card.avgRoi.toFixed(1)}x</span>
            )}
            <span className={styles.hint}>{card.hint}</span>
          </div>
        );
      })}
    </div>
  );
}
