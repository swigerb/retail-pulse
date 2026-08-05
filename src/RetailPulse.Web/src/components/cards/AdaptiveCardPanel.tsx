import { useState, useEffect, useCallback, useRef } from 'react';
import { makeStyles } from '@fluentui/react-components';
import * as signalR from '@microsoft/signalr';
import { CARD_COLORS, CARD_TYPE_CONFIG, CARD_LIFECYCLE_CONFIG } from '../../constants/agentRouting';
import { fetchActiveCards, submitVote } from '../../services/cardsApi';
import { resolveTelemetryHubUrl } from '../../config/telemetryHubUrl';
import type { AdaptiveCard, VoteChoice, DrillDownLevel } from '../../types';
import CardLifecycleIndicator from './CardLifecycleIndicator';
import VotingCard from './VotingCard';
import DrillDownCard from './DrillDownCard';

const CURRENT_USER_ID = 'current-user'; // placeholder until auth context is wired

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  headerTitle: {
    fontSize: '18px',
    fontWeight: '700',
    color: 'var(--color-text)',
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  count: {
    fontSize: '12px',
    fontWeight: '600',
    color: 'var(--color-text-muted)',
    background: 'rgba(255,255,255,0.08)',
    padding: '2px 8px',
    borderRadius: '10px',
  },
  loading: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '40px 0',
    fontSize: '14px',
    color: 'var(--color-text-muted)',
    gap: '10px',
  },
  spinner: {
    width: '18px',
    height: '18px',
    borderRadius: '50%',
    border: '2px solid rgba(255,255,255,0.1)',
    borderTopColor: CARD_COLORS.active,
    animationName: {
      from: { transform: 'rotate(0deg)' },
      to: { transform: 'rotate(360deg)' },
    },
    animationDuration: '0.8s',
    animationIterationCount: 'infinite',
    animationTimingFunction: 'linear',
  },
  error: {
    fontSize: '13px',
    color: CARD_COLORS.reject,
    textAlign: 'center',
    padding: '20px 0',
  },
  cardList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  cardRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '14px 16px',
    borderRadius: '10px',
    background: CARD_COLORS.cardBg,
    border: `1px solid ${CARD_COLORS.cardBorder}`,
    cursor: 'pointer',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.05)',
    },
  },
  cardTypeIcon:{
    fontSize: '22px',
    width: '36px',
    height: '36px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: '8px',
    background: 'rgba(255,255,255,0.06)',
    flexShrink: 0,
  },
  cardInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minWidth: 0,
  },
  cardTitle: {
    fontSize: '14px',
    fontWeight: '600',
    color: 'var(--color-text)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardMeta: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '11px',
    color: 'var(--color-text-muted)',
  },
  typeBadge: {
    fontSize: '10px',
    fontWeight: '600',
    padding: '2px 8px',
    borderRadius: '4px',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    whiteSpace: 'nowrap',
  },
  stateBadge: {
    fontSize: '10px',
    fontWeight: '700',
    padding: '2px 8px',
    borderRadius: '4px',
    whiteSpace: 'nowrap',
  },
  empty: {
    fontSize: '14px',
    color: 'var(--color-text-muted)',
    textAlign: 'center',
    padding: '40px 0',
  },
  detailOverlay: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  detailBack: {
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
    alignSelf: 'flex-start',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'rgba(255,255,255,0.08)',
      color: 'var(--color-text)',
    },
  },
});

export default function AdaptiveCardPanel() {
  const styles = useStyles();
  const [cards, setCards] = useState<AdaptiveCard[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedCard, setSelectedCard] = useState<AdaptiveCard | null>(null);

  // Mirror selectedCard into a ref so the SignalR effect can read the latest
  // selection without re-subscribing (which would tear the hub connection
  // down and back up on every card click — visible churn during demos).
  const selectedCardIdRef = useRef<string | null>(null);
  useEffect(() => {
    selectedCardIdRef.current = selectedCard?.id ?? null;
  }, [selectedCard]);

  const loadCards = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await fetchActiveCards();
      setCards(data);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load cards');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadCards();
  }, [loadCards]);

  // SignalR real-time updates — connection lifecycle is bound to the
  // component, NOT to selection. Card-selection state is read via a ref so
  // selecting a different card never tears down or rebuilds the hub.
  useEffect(() => {
    let connection: signalR.HubConnection | null = null;

    const applyUpdate = (updatedCard: AdaptiveCard) => {
      setCards((prev) =>
        prev.map((c) => (c.id === updatedCard.id ? updatedCard : c)),
      );
      if (selectedCardIdRef.current === updatedCard.id) {
        setSelectedCard(updatedCard);
      }
    };

    try {
      connection = new signalR.HubConnectionBuilder()
        .withUrl(resolveTelemetryHubUrl())
        .withAutomaticReconnect()
        .build();

      connection.on('card:action', applyUpdate);
      connection.on('card:lifecycle', applyUpdate);

      connection.start().catch(() => {
        /* silent — telemetry hub may not be available */
      });
    } catch {
      /* SignalR not available */
    }

    return () => {
      connection?.stop();
    };
  }, []);

  const handleVote = async (choice: VoteChoice) => {
    if (!selectedCard) return;
    try {
      await submitVote(selectedCard.id, choice);
    } catch {
      /* optimistic: SignalR will reconcile */
    }
  };

  // Detail view
  if (selectedCard) {
    const isVoting = selectedCard.type === 'voting';
    const isDrilldown = selectedCard.type === 'drilldown';

    return (
      <div className={styles.panel} data-testid="adaptive-card-panel">
        <div className={styles.detailOverlay}>
          <button className={styles.detailBack} onClick={() => setSelectedCard(null)}>
            ← Back to cards
          </button>

          {isVoting && (
            <VotingCard
              card={selectedCard}
              currentUserId={CURRENT_USER_ID}
              onVote={handleVote}
            />
          )}

          {isDrilldown && (
            <DrillDownCard
              card={selectedCard}
              levels={(selectedCard.data?.levels as DrillDownLevel[]) ?? []}
            />
          )}

          {/* Fallback for dashboard/briefing — show basic info */}
          {!isVoting && !isDrilldown && (
            <div
              style={{
                background: CARD_COLORS.cardBg,
                border: `1px solid ${CARD_COLORS.cardBorder}`,
                borderRadius: '12px',
                padding: '20px',
                display: 'flex',
                flexDirection: 'column',
                gap: '12px',
              }}
            >
              <span style={{ fontSize: '17px', fontWeight: '700', color: 'var(--color-text)' }}>
                {selectedCard.title}
              </span>
              <span style={{ fontSize: '13px', color: 'var(--color-text-muted)', lineHeight: '1.5' }}>
                {selectedCard.summary}
              </span>
              <CardLifecycleIndicator
                currentState={selectedCard.state}
                stateChangedAt={selectedCard.stateChangedAt}
              />
            </div>
          )}
        </div>
      </div>
    );
  }

  // List view
  return (
    <div className={styles.panel} data-testid="adaptive-card-panel">
      <div className={styles.header}>
        <span className={styles.headerTitle}>
          🃏 Adaptive Cards
          <span className={styles.count}>{cards.length}</span>
        </span>
      </div>

      {loading && (
        <div className={styles.loading}>
          <div className={styles.spinner} />
          Loading cards…
        </div>
      )}

      {error && <div className={styles.error}>⚠️ {error}</div>}

      {!loading && !error && cards.length === 0 && (
        <div className={styles.empty}>No active cards</div>
      )}

      {!loading && !error && cards.length > 0 && (
        <div className={styles.cardList}>
          {cards.map((card) => {
            const typeConfig = CARD_TYPE_CONFIG[card.type] ?? { emoji: '📄', label: card.type };
            const stateConfig = CARD_LIFECYCLE_CONFIG[card.state];

            return (
              <div
                key={card.id}
                className={styles.cardRow}
                onClick={() => setSelectedCard(card)}
                data-testid="card-list-item"
              >
                <div className={styles.cardTypeIcon}>{typeConfig.emoji}</div>
                <div className={styles.cardInfo}>
                  <span className={styles.cardTitle}>{card.title}</span>
                  <div className={styles.cardMeta}>
                    <span
                      className={styles.typeBadge}
                      style={{
                        background: 'rgba(255,255,255,0.06)',
                        color: 'var(--color-text-muted)',
                      }}
                    >
                      {typeConfig.label}
                    </span>
                    <span
                      className={styles.stateBadge}
                      style={{
                        background: stateConfig.bg,
                        color: stateConfig.color,
                      }}
                    >
                      {stateConfig.label}
                    </span>
                    {card.escalated && (
                      <span style={{ color: CARD_COLORS.escalation, fontSize: '11px' }}>⚠️ Escalated</span>
                    )}
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
