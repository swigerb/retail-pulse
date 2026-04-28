import { useState, useRef, useEffect } from 'react';
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
import { PatronLogo } from './PatronLogo';
import { ChartRenderer } from './ChartRenderer';

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  spans?: AgentSpan[];
  charts?: ChartSpec[];
}

interface Props {
  // Spans are displayed per-message via SpansSummary; no need to lift them up
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
    color: '#C8A951',
    backgroundColor: 'rgba(200, 169, 81, 0.12)',
    padding: '5px 12px',
    borderRadius: '20px',
    border: '1px solid rgba(200, 169, 81, 0.25)',
    cursor: 'pointer',
    transition: 'background 0.2s ease',
    ':hover': {
      backgroundColor: 'rgba(200, 169, 81, 0.2)',
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
    backgroundColor: '#1A1A1A',
    border: '1px solid rgba(255, 255, 255, 0.08)',
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
      backgroundColor: '#242424',
    },
  },
  spanIcon: {
    flexShrink: '0',
  },
  spanName: {
    fontWeight: '500',
    color: '#F5F5F0',
    flex: '1',
    minWidth: '0',
  },
  spanDuration: {
    fontFamily: "'Courier New', monospace",
    fontSize: '11px',
    color: '#A0A0A0',
    flexShrink: '0',
  },
  spanDetail: {
    fontSize: '11px',
    color: '#666666',
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
      <button className={styles.toggle} onClick={() => setExpanded(!expanded)}>
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
              <span className={styles.spanDuration}>{span.durationMs}ms</span>
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
    backgroundColor: '#080808',
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
      background: 'rgba(200, 169, 81, 0.2)',
      borderRadius: '3px',
    },
    '::-webkit-scrollbar-thumb:hover': {
      background: 'rgba(200, 169, 81, 0.35)',
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
    color: '#A0A0A0',
    fontSize: '15px',
    marginBottom: '32px',
  },
  suggestedQueries: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '10px',
    maxWidth: '640px',
    width: '100%',
  },
  suggestedQuery: {
    background: '#1A1A1A',
    border: '1px solid rgba(255, 255, 255, 0.08)',
    color: '#A0A0A0',
    padding: '14px 18px',
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '13px',
    textAlign: 'left',
    transition: 'all 0.2s ease',
    lineHeight: '1.4',
    ':hover': {
      background: '#242424',
      border: '1px solid rgba(200, 169, 81, 0.2)',
      color: '#D4B96A',
      transform: 'translateY(-1px)',
      boxShadow: '0 4px 24px rgba(0, 0, 0, 0.4)',
    },
    ':disabled': {
      opacity: '0.4',
      cursor: 'not-allowed',
      transform: 'none',
    },
  },
  suggestedHighlight: {
    border: '1px solid rgba(200, 169, 81, 0.2)',
    background: 'rgba(200, 169, 81, 0.12)',
    color: '#D4B96A',
    gridColumn: '1 / -1',
    fontSize: '14px',
    padding: '16px 20px',
  },
  suggestedStar: {
    color: '#C8A951',
    marginRight: '6px',
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
    background: 'linear-gradient(135deg, #242424 0%, #1A1A1A 100%)',
    border: '1px solid rgba(200, 169, 81, 0.2)',
    color: '#F5F5F0',
    borderBottomRightRadius: '4px',
    whiteSpace: 'pre-wrap',
  },
  assistantCard: {
    background: '#1A1A1A',
    border: '1px solid rgba(255, 255, 255, 0.08)',
    color: '#F5F5F0',
    borderBottomLeftRadius: '4px',
  },
  inputArea: {
    display: 'flex',
    gap: '10px',
    padding: '16px 24px',
    backgroundColor: '#0D0D0D',
    borderTop: '1px solid rgba(255, 255, 255, 0.08)',
  },
  loadingContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    background: '#1A1A1A',
    border: '1px solid rgba(255, 255, 255, 0.08)',
    borderRadius: '12px',
    color: '#A0A0A0',
    fontSize: '14px',
  },
});

export function ChatPanel(_props: Props) {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [sessionId, setSessionId] = useState<string | undefined>();
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const styles = useChatStyles();

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleSend = async () => {
    if (!input.trim() || loading) return;

    const userMessage = input.trim();
    setInput('');
    setMessages(prev => [...prev, { role: 'user', content: userMessage }]);
    setLoading(true);

    try {
      const response = await sendMessage({ message: userMessage, sessionId });
      setSessionId(response.sessionId);
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: response.reply, spans: response.spans, charts: response.charts }
      ]);
    } catch (err) {
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: `Error: ${err instanceof Error ? err.message : 'Unknown error'}` }
      ]);
    } finally {
      setLoading(false);
    }
  };

  const handleSuggestedClick = async (query: string) => {
    if (loading) return;
    setMessages(prev => [...prev, { role: 'user', content: query }]);
    setLoading(true);
    try {
      const response = await sendMessage({ message: query, sessionId });
      setSessionId(response.sessionId);
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: response.reply, spans: response.spans, charts: response.charts }
      ]);
    } catch (err) {
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: `Error: ${err instanceof Error ? err.message : 'Unknown error'}` }
      ]);
    } finally {
      setLoading(false);
    }
  };

  const suggestedQueries = [
    { text: "Analyze the shipment pipeline for Patron Silver in Florida", highlight: true },
    { text: "How is Patrón Silver performing in Florida?" },
    { text: "Compare Angel's Envy performance across New York and Illinois" },
    { text: "What's the field sentiment for Grey Goose in California?" },
    { text: "Which brands are growth leaders nationally?" },
    { text: "Give me a national overview of Cazadores" },
  ];

  return (
    <div className={styles.panel}>
      <div className={styles.messages}>
        {messages.length === 0 && (
          <div className={styles.welcome}>
            <div className={styles.welcomeLogo}><PatronLogo size={56} /></div>
            <Text className={styles.welcomeText}>
              Ask me about brand performance, depletion trends, or field sentiment across the Bacardi portfolio.
            </Text>
            <div className={styles.suggestedQueries}>
              {suggestedQueries.map((q, i) => (
                <button
                  key={i}
                  className={`${styles.suggestedQuery} ${q.highlight ? styles.suggestedHighlight : ''}`}
                  onClick={() => handleSuggestedClick(q.text)}
                  disabled={loading}
                >
                  {q.highlight && <span className={styles.suggestedStar}>★</span>}
                  {q.text}
                </button>
              ))}
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
              color={msg.role === 'user' ? 'colorful' : 'gold'}
              name={msg.role === 'user' ? 'User' : 'Patron Pulse'}
              icon={msg.role === 'user' ? undefined : { children: 'P' }}
              style={{
                backgroundColor: msg.role === 'assistant' ? '#C8A951' : undefined,
                color: msg.role === 'assistant' ? '#0D0D0D' : undefined,
              }}
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
                <ChartRenderer charts={msg.charts} />
              )}
            </div>
          </div>
        ))}

        {loading && (
          <div className={`${styles.message} ${styles.messageAssistant}`}>
            <Avatar
              size={36}
              color="gold"
              name="Patron Pulse"
              icon={{ children: 'P' }}
              style={{ backgroundColor: '#C8A951', color: '#0D0D0D' }}
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
        <Input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && handleSend()}
          placeholder="Ask about brand performance..."
          disabled={loading}
          style={{ flex: 1 }}
        />
        <Button
          appearance="primary"
          icon={<Send24Regular />}
          onClick={handleSend}
          disabled={loading || !input.trim()}
          style={{
            background: 'linear-gradient(135deg, #C8A951 0%, #B89A3F 100%)',
            color: '#080808',
          }}
        >
          Send
        </Button>
      </div>
    </div>
  );
}
