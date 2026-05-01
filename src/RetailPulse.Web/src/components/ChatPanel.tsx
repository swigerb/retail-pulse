import { useState, useRef, useEffect, useCallback, lazy, Suspense } from 'react';
import ReactMarkdown from 'react-markdown';
import {
  Input,
  Button,
  Card,
  Avatar,
  Spinner,
  Text,
  makeStyles,
} from '@fluentui/react-components';
import { Send24Regular, ChevronRight16Regular } from '@fluentui/react-icons';
import type { AgentSpan, ChartSpec } from '../types';
import { sendMessage } from '../services/api';
import { joinTelemetrySession } from '../services/telemetryHub';
import { BrandLogo } from './BrandLogo';

const ChartRenderer = lazy(() => import('./ChartRenderer'));

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  spans?: AgentSpan[];
  charts?: ChartSpec[];
}

const SPAN_ICONS: Record<string, string> = {
  thought: '🧠',
  tool_call: '🔧',
  tool_result: '📦',
  response: '💬',
  agent_delegation: '🤝',
  agent_call: '📡',
  agent_response: '✅',
};

// Stable inline-style constants — hoisted so they don't re-allocate per render.
const FLEX_ONE_STYLE: React.CSSProperties = { flex: 1 };
const ASSISTANT_AVATAR_STYLE: React.CSSProperties = {
  backgroundColor: 'var(--brand-primary)',
  color: 'var(--color-bg-elevated)',
};
const ASSISTANT_LOADING_AVATAR_STYLE: React.CSSProperties = {
  backgroundColor: 'var(--brand-primary)',
  color: '#fff',
};
const SEND_BUTTON_STYLE: React.CSSProperties = {
  background: 'linear-gradient(135deg, var(--brand-primary) 0%, var(--brand-accent) 100%)',
  color: '#ffffff',
};
const ASSISTANT_AVATAR_ICON = { children: 'R' } as const;

interface PromptCategory {
  id: string;
  label: string;
  emoji: string;
  prompts: string[];
}

const PROMPT_CATEGORIES: ReadonlyArray<PromptCategory> = [
  {
    id: 'general',
    label: 'General Retail',
    emoji: '📊',
    prompts: [
      'Compare depletion trends across all regions for this quarter',
      'Which brands are growing fastest year-over-year across the portfolio?',
      'Show me field sentiment for our top 3 brands in the Southeast',
    ],
  },
  {
    id: 'grocery',
    label: 'Grocery',
    emoji: '🛒',
    prompts: [
      'How are FreshMart depletions trending in the Northeast this quarter?',
      'Compare Harvest Table vs FreshMart sell-through rates by region',
      'What is the field sentiment for Harvest Table Meal Kits in the Midwest?',
    ],
  },
  {
    id: 'qsr',
    label: 'Quick-Serve Restaurants',
    emoji: '🍔',
    prompts: [
      'How is Apex Grill performing in the Southwest this quarter?',
      'Compare Coastline Tacos vs Apex Grill depletions across all regions',
      'What is the field sentiment for Coastline Tacos in the West Coast?',
    ],
  },
  {
    id: 'home-improvement',
    label: 'Home Improvement',
    emoji: '🏠',
    prompts: [
      'Show me Pinnacle Hardware depletion stats in the Midwest for Q1',
      'How is Summit Outdoor performing in the Southeast vs West Coast?',
      'What is the field sentiment for Pinnacle Hardware Power Tools in the Southwest?',
    ],
  },
  {
    id: 'office-supply',
    label: 'Office Supply',
    emoji: '📎',
    prompts: [
      'How are ClearDesk depletions trending in the Northeast this quarter?',
      'Compare ClearDesk Technology vs Paper Products sell-through by region',
      'What is the field sentiment for ClearDesk in the Southeast?',
    ],
  },
  {
    id: 'furniture',
    label: 'Furniture',
    emoji: '🛋️',
    prompts: [
      'Show me Urban Living depletion trends across all regions this quarter',
      'Compare Foundry Home vs Urban Living performance in the West Coast',
      'What is the field sentiment for Urban Living in the Pacific Northwest?',
    ],
  },
  {
    id: 'charts',
    label: 'Charts',
    emoji: '📈',
    prompts: [
      'Create a line chart showing Sierra Gold Tequila depletion trends across all regions',
      'Show me a bar chart comparing depletion velocity for all spirits brands in the Northeast',
      'Create a pie chart showing market share breakdown for our grocery brands nationally',
      'Show a grouped bar chart comparing FreshMart and Harvest Table across all regions',
      'Create a donut chart of Apex Grill variant mix in the Southwest',
      'Show a horizontal bar chart ranking all brands by depletion growth rate',
      'Create a table showing depletion stats for all home improvement brands by region',
      'Show a gauge chart for Pinnacle Hardware inventory health in the Midwest',
    ],
  },
];

const useSpanStyles = makeStyles({
  summary: {
    marginTop: '6px',
    alignSelf: 'flex-start',
  },
  toggle: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '11px',
    color: 'var(--brand-accent)',
    backgroundColor: 'var(--brand-accent-soft)',
    padding: '5px 12px',
    borderRadius: '20px',
    border: '1px solid var(--brand-accent-border)',
    cursor: 'pointer',
    transition: 'background 0.2s ease',
    ':hover': {
      backgroundColor: 'var(--brand-accent-soft-hover)',
    },
  },
  chevron: {
    fontSize: '9px',
    transition: 'transform 0.2s ease',
  },
  chevronExpanded: {
    transform: 'rotate(90deg)',
  },
  detail: {
    marginTop: '6px',
    backgroundColor: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '8px',
    padding: '8px',
    fontSize: '12px',
  },
  spanRow: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '8px',
    padding: '4px 6px',
    borderRadius: '4px',
    flexWrap: 'wrap',
    ':hover': {
      backgroundColor: 'var(--color-surface-hover)',
    },
  },
  spanIcon: {
    flexShrink: '0',
  },
  spanName: {
    fontWeight: '500',
    color: 'var(--color-text)',
    flex: '1',
    minWidth: '0',
  },
  spanDuration: {
    fontFamily: "'Courier New', monospace",
    fontSize: '11px',
    color: 'var(--color-text-muted)',
    flexShrink: '0',
  },
  spanDetail: {
    fontSize: '11px',
    color: 'var(--color-text-subtle)',
    flexBasis: '100%',
    paddingLeft: '24px',
    marginTop: '2px',
  },
});

function SpansSummary({ spans }: { spans: AgentSpan[] }) {
  const [expanded, setExpanded] = useState(false);
  const styles = useSpanStyles();
  const totalMs = spans.reduce((sum, s) => sum + s.durationMs, 0);
  const toolCalls = spans.filter(s => s.type === 'tool_call');
  const agentCalls = spans.filter(s => s.type === 'agent_call' || s.type === 'agent_delegation');

  const summary = [
    `${spans.length} spans`,
    toolCalls.length > 0 ? `${toolCalls.length} tool call${toolCalls.length > 1 ? 's' : ''}` : '',
    agentCalls.length > 0 ? `${agentCalls.length} agent call${agentCalls.length > 1 ? 's' : ''}` : '',
    `${(totalMs / 1000).toFixed(1)}s total`,
  ].filter(Boolean).join(' · ');

  return (
    <div className={styles.summary}>
      <button
        className={styles.toggle}
        onClick={() => setExpanded(!expanded)}
        aria-expanded={expanded}
        aria-label={expanded ? 'Collapse span details' : 'Expand span details'}
      >
        <span className={styles.spanIcon}>📊</span>
        <span>{summary}</span>
        <span className={`${styles.chevron} ${expanded ? styles.chevronExpanded : ''}`}>
          <ChevronRight16Regular />
        </span>
      </button>
      {expanded && (
        <div className={styles.detail}>
          {spans.map((span, i) => (
            <div key={i} className={styles.spanRow}>
              <span className={styles.spanIcon}>{SPAN_ICONS[span.type] ?? '📌'}</span>
              <span className={styles.spanName}>{span.name}</span>
              <span className={styles.spanDuration}>{span.durationMs > 0 ? `${span.durationMs}ms` : '—'}</span>
              {span.detail && <span className={styles.spanDetail}>{span.detail}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const useChatStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    height: '100%',
    backgroundColor: 'var(--color-bg)',
  },
  messages: {
    flex: '1',
    overflowY: 'auto',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    scrollBehavior: 'smooth',
    '::-webkit-scrollbar': {
      width: '6px',
    },
    '::-webkit-scrollbar-track': {
      background: 'transparent',
    },
    '::-webkit-scrollbar-thumb': {
      background: 'var(--color-scrollbar)',
      borderRadius: '3px',
    },
    '::-webkit-scrollbar-thumb:hover': {
      background: 'var(--color-scrollbar-hover)',
    },
    '@media (max-width: 600px)': {
      padding: '16px',
    },
  },
  welcome: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    textAlign: 'center',
    padding: '60px 20px',
    flex: '1',
  },
  welcomeLogo: {
    marginBottom: '24px',
  },
  welcomeText: {
    color: 'var(--color-text-muted)',
    fontSize: '15px',
    marginBottom: '32px',
  },
  suggestedQueries: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    maxWidth: '720px',
    width: '100%',
  },
  categoryChips: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    justifyContent: 'center',
  },
  categoryChip: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 16px',
    borderRadius: '20px',
    border: '1px solid var(--color-border)',
    background: 'var(--color-surface)',
    color: 'var(--color-text-muted)',
    cursor: 'pointer',
    fontSize: '13px',
    fontWeight: '500',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'var(--brand-accent-soft)',
      border: '1px solid var(--brand-accent-border)',
      color: 'var(--brand-accent-light)',
    },
  },
  categoryChipActive: {
    background: 'var(--brand-accent-soft)',
    border: '1px solid var(--brand-accent)',
    color: 'var(--brand-accent)',
    fontWeight: '600',
  },
  promptGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '10px',
    '@media (max-width: 640px)': {
      gridTemplateColumns: '1fr',
    },
  },
  suggestedQuery: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    color: 'var(--color-text-muted)',
    padding: '14px 18px',
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '13px',
    textAlign: 'left',
    transition: 'all 0.2s ease',
    lineHeight: '1.4',
    ':hover': {
      background: 'var(--color-surface-hover)',
      border: '1px solid var(--brand-accent-soft-hover)',
      color: 'var(--brand-accent-light)',
      transform: 'translateY(-1px)',
      boxShadow: '0 4px 24px rgba(0, 0, 0, 0.4)',
    },
    ':disabled': {
      opacity: '0.4',
      cursor: 'not-allowed',
      transform: 'none',
    },
  },
  message: {
    display: 'flex',
    gap: '12px',
    maxWidth: '85%',
    animation: 'messageIn 0.3s ease',
  },
  messageUser: {
    alignSelf: 'flex-end',
    flexDirection: 'row-reverse',
  },
  messageAssistant: {
    alignSelf: 'flex-start',
  },
  messageContent: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  messageCard: {
    padding: '12px 16px',
    borderRadius: '12px',
    fontSize: '14px',
    lineHeight: '1.6',
    wordBreak: 'break-word',
  },
  userCard: {
    background: 'linear-gradient(135deg, var(--color-surface-hover) 0%, var(--color-surface) 100%)',
    border: '1px solid var(--brand-accent-soft-hover)',
    color: 'var(--color-text)',
    borderBottomRightRadius: '4px',
    whiteSpace: 'pre-wrap',
  },
  assistantCard: {
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    color: 'var(--color-text)',
    borderBottomLeftRadius: '4px',
  },
  inputArea: {
    display: 'flex',
    gap: '10px',
    padding: '16px 24px',
    backgroundColor: 'var(--color-bg-elevated)',
    borderTop: '1px solid var(--color-border)',
    '@media (max-width: 600px)': {
      padding: '12px 16px',
    },
  },
  loadingContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    background: 'var(--color-surface)',
    border: '1px solid var(--color-border)',
    borderRadius: '12px',
    color: 'var(--color-text-muted)',
    fontSize: '14px',
  },
});

export function ChatPanel() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [sessionId] = useState<string>(() => crypto.randomUUID().replace(/-/g, ''));
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const styles = useChatStyles();

  // Track mounted state and abort in-flight requests on unmount so async work
  // doesn't call setState on a torn-down component (e.g. when Dashboard
  // increments chatKey to start a "New Chat").
  const isMountedRef = useRef(true);
  const abortControllerRef = useRef<AbortController | null>(null);

  useEffect(() => {
    isMountedRef.current = true;
    // Pre-join the SignalR session group so real-time telemetry works from the first message
    joinTelemetrySession(sessionId);
    return () => {
      isMountedRef.current = false;
      abortControllerRef.current?.abort();
    };
  }, [sessionId]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const sendChatMessage = useCallback(
    async (message: string) => {
      const trimmed = message.trim();
      if (!trimmed) return;

      // Cancel any prior in-flight request before starting a new one.
      abortControllerRef.current?.abort();
      const controller = new AbortController();
      abortControllerRef.current = controller;

      setMessages(prev => [...prev, { role: 'user', content: trimmed }]);
      setLoading(true);

      try {
        const response = await sendMessage(
          { message: trimmed, sessionId },
          { signal: controller.signal },
        );
        if (!isMountedRef.current || controller.signal.aborted) return;
        setMessages(prev => [
          ...prev,
          { role: 'assistant', content: response.reply, spans: response.spans, charts: response.charts },
        ]);
      } catch (err) {
        if (!isMountedRef.current || controller.signal.aborted) return;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setMessages(prev => [
          ...prev,
          { role: 'assistant', content: `Error: ${err instanceof Error ? err.message : 'Unknown error'}` },
        ]);
      } finally {
        if (isMountedRef.current && abortControllerRef.current === controller) {
          setLoading(false);
          abortControllerRef.current = null;
        }
      }
    },
    [sessionId],
  );

  const handleSend = useCallback(async () => {
    if (!input.trim() || loading) return;
    const userMessage = input.trim();
    setInput('');
    await sendChatMessage(userMessage);
  }, [input, loading, sendChatMessage]);

  const handleSuggestedClick = useCallback(
    async (query: string) => {
      if (loading) return;
      await sendChatMessage(query);
    },
    [loading, sendChatMessage],
  );

  const [selectedCategory, setSelectedCategory] = useState<string | null>(null);

  const visiblePrompts = selectedCategory
    ? PROMPT_CATEGORIES.filter(c => c.id === selectedCategory)
    : PROMPT_CATEGORIES;

  return (
    <div className={styles.panel}>
      <div className={styles.messages}>
        {messages.length === 0 && (
          <div className={styles.welcome}>
            <div className={styles.welcomeLogo}><BrandLogo size={56} /></div>
            <Text className={styles.welcomeText}>
              Ask me about sales performance, inventory trends, or customer insights across your retail portfolio.
            </Text>
            <div className={styles.suggestedQueries}>
              <div className={styles.categoryChips}>
                <button
                  className={`${styles.categoryChip} ${selectedCategory === null ? styles.categoryChipActive : ''}`}
                  onClick={() => setSelectedCategory(null)}
                >
                  🏪 All
                </button>
                {PROMPT_CATEGORIES.map((cat) => (
                  <button
                    key={cat.id}
                    className={`${styles.categoryChip} ${selectedCategory === cat.id ? styles.categoryChipActive : ''}`}
                    onClick={() => setSelectedCategory(cat.id)}
                  >
                    {cat.emoji} {cat.label}
                  </button>
                ))}
              </div>
              <div className={styles.promptGrid}>
                {visiblePrompts.map((cat) =>
                  (selectedCategory ? cat.prompts : cat.prompts.slice(0, 1)).map((prompt, i) => (
                    <button
                      key={`${cat.id}-${i}`}
                      className={styles.suggestedQuery}
                      onClick={() => handleSuggestedClick(prompt)}
                      disabled={loading}
                    >
                      <span>{cat.emoji}</span> {prompt}
                    </button>
                  ))
                )}
              </div>
            </div>
          </div>
        )}

        {messages.map((msg, i) => (
          <div
            key={i}
            className={`${styles.message} ${msg.role === 'user' ? styles.messageUser : styles.messageAssistant}`}
          >
            <Avatar
              size={36}
              color={msg.role === 'user' ? 'colorful' : 'brand'}
              name={msg.role === 'user' ? 'User' : 'Retail Pulse'}
              icon={msg.role === 'user' ? undefined : ASSISTANT_AVATAR_ICON}
              style={msg.role === 'assistant' ? ASSISTANT_AVATAR_STYLE : undefined}
            />
            <div className={styles.messageContent}>
              <Card
                className={`${styles.messageCard} ${msg.role === 'user' ? styles.userCard : styles.assistantCard}`}
                appearance="subtle"
              >
                {msg.role === 'assistant' ? (
                  <div className="markdown-body">
                    <ReactMarkdown>{msg.content}</ReactMarkdown>
                  </div>
                ) : (
                  <div>{msg.content}</div>
                )}
              </Card>
              {msg.spans && msg.spans.length > 0 && (
                <SpansSummary spans={msg.spans} />
              )}
              {msg.charts && msg.charts.length > 0 && (
                <Suspense fallback={<div className={styles.loadingContainer}><Spinner size="tiny" />Loading charts…</div>}>
                  <ChartRenderer charts={msg.charts} />
                </Suspense>
              )}
            </div>
          </div>
        ))}

        {loading && (
          <div className={`${styles.message} ${styles.messageAssistant}`}>
            <Avatar
              size={36}
              color="brand"
              name="Retail Pulse"
              icon={ASSISTANT_AVATAR_ICON}
              style={ASSISTANT_LOADING_AVATAR_STYLE}
            />
            <div className={styles.messageContent}>
              <div className={styles.loadingContainer}>
                <Spinner size="tiny" />
                Analyzing data...
              </div>
            </div>
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      <div className={styles.inputArea}>
        <label htmlFor="chat-input" className="visually-hidden">
          Ask about retail performance
        </label>
        <Input
          id="chat-input"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && handleSend()}
          placeholder="Ask about retail performance..."
          disabled={loading}
          style={FLEX_ONE_STYLE}
        />
        <Button
          appearance="primary"
          icon={<Send24Regular style={{ color: '#ffffff' }} />}
          onClick={handleSend}
          disabled={loading || !input.trim()}
          aria-label="Send message"
          style={SEND_BUTTON_STYLE}
        >
          Send
        </Button>
      </div>
    </div>
  );
}
