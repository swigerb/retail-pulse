import { useState, useRef, useEffect, useCallback, lazy, Suspense } from 'react';
import { ErrorBoundary } from './ErrorBoundary';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
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
import type { AgentSpan, ChatHistoryMessage, ChartSpec, RoutingInfo, TokenUsage, MemoryContext, ApprovalRequest, ApprovalDecision, CacheInfo, ForceableExecutionPath } from '../types';
import type { SendMessageOptions } from '../services/api';
import { sendMessage, isErrorReply, isChatRequestTimeoutError } from '../services/api';
import { joinTelemetrySession, onProgress } from '../services/telemetryHub';
import { cancelChatSession, cancelPlan } from '../services/executionControlApi';
import { useConnectionStatus } from '../hooks/useConnectionStatus';
import { ConnectionStatusIndicator } from './ConnectionStatusIndicator';
import { TimeoutDialog } from './TimeoutDialog';
import { activeAuthMode } from '../auth/activeProvider';
import { BrandLogo } from './BrandLogo';
import { AgentRoutingIndicator } from './AgentRoutingIndicator';
import { MemoryIndicator } from './MemoryIndicator';
import { ApprovalCard } from './ApprovalCard';
import { StreamingMessage, CacheIndicator } from './streaming';
import { ProgressIndicator } from './ProgressIndicator';
import type { ProgressStep } from './ProgressIndicator';
import { BlockedRequestMessage, WithheldOutputMessage } from './guardrails';
import { detectSafetyRefusal } from '../utils/safetyDisplay';
import type { SafetyBlockDisplayModel } from '../types';
import { PromptLibrary } from './PromptLibrary';
import { PROMPT_CATEGORIES, type PromptCategory } from '../constants/prompts';
import { sanitizeMessage } from '../utils';
import { PlanView } from './plan';
import type { PlanController } from '../state/usePlanController';
import { isPlanRunning } from '../state/planReducer';

const ChartRenderer = lazy(() => import('./ChartRenderer'));

interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  spans?: AgentSpan[];
  charts?: ChartSpec[];
  routing?: RoutingInfo;
  totalDurationMs?: number;
  tokenUsage?: TokenUsage;
  memoryContext?: MemoryContext;
  approval?: ApprovalRequest;
  isStreaming?: boolean;
  cacheInfo?: CacheInfo;
  blocked?: { reason: string; suggestion?: string; display?: SafetyBlockDisplayModel };
  /**
   * When set, this message is a plan-first response — render the PlanView
   * with matching planId in place of the plain reply. Fast-path (single-shot)
   * responses NEVER carry planId, so their bubble stays exactly as today
   * with no plan chrome.
   */
  planId?: string;
}

interface ChatPanelProps {
  onResponseReceived?: (response: { totalDurationMs?: number; tokenUsage?: TokenUsage; routing?: RoutingInfo }) => void;
  approvals?: ApprovalRequest[];
  onApprovalResolved?: (id: string, decision: ApprovalDecision) => void;
  /**
   * Categories rendered in the welcome-state chip grid and the persistent
   * Prompt Library popover. Defaults to the built-in `PROMPT_CATEGORIES`
   * so the welcome state paints synchronously on the very first render,
   * matching the pre-#108 baseline. Dashboard swaps in the active
   * content pack's categories once `/api/pack/starting-tasks` resolves.
   */
  promptCategories?: ReadonlyArray<PromptCategory>;
  /**
   * Optional plan controller supplied by the Dashboard. When provided,
   * plan-first responses render the interactive PlanView inline instead of
   * the plain markdown reply. Omitting it leaves the panel in the legacy
   * "chat only" mode used by tests that mount ChatPanel in isolation.
   */
  planController?: PlanController;
  /** Live SignalR connection status for the plan view banner. */
  planConnected?: boolean;
}

const SPAN_ICONS: Record<string, string> = {
  thought: '🧠',
  tool_call: '🔧',
  tool_result: '📥',
  response: '💬',
  agent_delegation: '🤝',
  agent_call: '📡',
  agent_response: '✅',
  routing: '🔀',
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

function SpansSummary({ spans, totalDurationMs, tokenUsage }: { spans: AgentSpan[]; totalDurationMs?: number; tokenUsage?: TokenUsage }) {
  const [expanded, setExpanded] = useState(false);
  const styles = useSpanStyles();
  const totalMs = totalDurationMs ?? spans.reduce((sum, s) => sum + s.durationMs, 0);
  const toolCalls = spans.filter(s => s.type === 'tool_call');
  const agentCalls = spans.filter(s => s.type === 'agent_call' || s.type === 'agent_delegation');

  const summary = [
    `📊 ${spans.length} spans`,
    toolCalls.length > 0 ? `🔧 ${toolCalls.length} tool call${toolCalls.length > 1 ? 's' : ''}` : '',
    agentCalls.length > 0 ? `${agentCalls.length} agent call${agentCalls.length > 1 ? 's' : ''}` : '',
    `⏱️ ${(totalMs / 1000).toFixed(1)}s total`,
    tokenUsage ? `🪙 ${tokenUsage.totalTokens.toLocaleString()} tokens` : '',
    tokenUsage?.estimatedCostUsd != null ? `💲~${tokenUsage.estimatedCostUsd < 0.01 ? `$${tokenUsage.estimatedCostUsd.toFixed(4)}` : `$${tokenUsage.estimatedCostUsd.toFixed(2)}`}` : '',
  ].filter(Boolean).join(' · ');

  return (
    <div className={styles.summary}>
      <button
        className={styles.toggle}
        onClick={() => setExpanded(!expanded)}
        aria-expanded={expanded}
        aria-label={expanded ? 'Collapse span details' : 'Expand span details'}
      >
        <span>{summary}</span>
        <span className={`${styles.chevron} ${expanded ? styles.chevronExpanded : ''}`}>
          <ChevronRight16Regular />
        </span>
      </button>
      {expanded && (
        <div className={styles.detail}>
          {spans.map((span, i) => (
            <div key={`span-${span.name}-${i}`} className={styles.spanRow}>
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
    marginBottom: '16px',
  },
  welcomeHeroLogo: {
    width: 'min(100%, 320px)',
    height: 'auto',
    display: 'block',
    margin: '0 auto 24px',
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
  suggestedEmpty: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    padding: '24px 16px',
    textAlign: 'center',
    color: 'var(--color-text-muted)',
    fontSize: '13px',
    lineHeight: '1.5',
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
    alignItems: 'center',
    padding: '16px 24px',
    backgroundColor: 'var(--color-bg-elevated)',
    borderTop: '1px solid var(--color-border)',
    '@media (max-width: 600px)': {
      padding: '12px 16px',
    },
  },
  executionPathSelect: {
    flexShrink: '0',
    height: '32px',
    padding: '0 26px 0 10px',
    borderRadius: '8px',
    border: '1px solid var(--color-border)',
    background: 'var(--color-surface)',
    color: 'var(--color-text-muted)',
    fontSize: '12px',
    fontWeight: '500',
    cursor: 'pointer',
    appearance: 'none',
    backgroundImage:
      "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6'><path fill='%239aa0a6' d='M0 0l5 6 5-6z'/></svg>\")",
    backgroundRepeat: 'no-repeat',
    backgroundPosition: 'right 10px center',
    transition: 'all 0.2s ease',
    ':hover': {
      background: 'var(--color-surface-hover)',
      border: '1px solid var(--brand-accent-soft-hover)',
      color: 'var(--brand-accent-light)',
      backgroundImage:
        "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6'><path fill='%239aa0a6' d='M0 0l5 6 5-6z'/></svg>\")",
      backgroundRepeat: 'no-repeat',
      backgroundPosition: 'right 10px center',
    },
    ':focus-visible': {
      outline: '2px solid var(--brand-accent)',
      outlineOffset: '1px',
    },
    ':disabled': {
      opacity: '0.5',
      cursor: 'not-allowed',
    },
  },
  executionPathForced: {
    color: 'var(--brand-accent)',
    border: '1px solid var(--brand-accent-border)',
    background: 'var(--brand-accent-soft)',
    fontWeight: '600',
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

export function ChatPanel({
  onResponseReceived,
  approvals,
  onApprovalResolved,
  promptCategories = PROMPT_CATEGORIES,
  planController,
  planConnected,
}: ChatPanelProps) {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [loadingText, setLoadingText] = useState<string>('Thinking...');
  const [progressSteps, setProgressSteps] = useState<ProgressStep[]>([]);
  const [sessionId] = useState<string>(() => crypto.randomUUID().replace(/-/g, ''));
  // "auto" is the default — omit the field so the backend chooses. Only
  // `fast` / `plan` are ever sent to the server; council keeps its own
  // dedicated trigger and is not a valid override server-side.
  const [forcePath, setForcePath] = useState<'auto' | ForceableExecutionPath>('auto');
  // Anonymous sessions cannot force a path (backend ignores the field for
  // anonymous users). Hide the selector so we don't present a misleading
  // control that the server would silently drop.
  const supportsExecutionPathOverride = activeAuthMode !== 'anonymous';
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const styles = useChatStyles();

  // Real-time channel status surfaced next to the composer so a dropped
  // hub is visible where the user actually types, not buried in a drawer.
  const { status: hubStatus, stalled: hubStalled } = useConnectionStatus();

  // Timeout dialog state (issue #92). Populated when sendMessage throws
  // ChatRequestTimeoutError so the user can Retry or Abandon instead of
  // staring at a spinner that will never resolve.
  const [timeoutInfo, setTimeoutInfo] = useState<{ prompt: string; timeoutMs: number } | null>(null);

  // Retains the most recent user prompt so the timeout dialog's Retry
  // button can replay the same request without asking the user to retype.
  const lastPromptRef = useRef<string | null>(null);

  // Track mounted state and abort in-flight requests on unmount so async work
  // doesn't call setState on a torn-down component (e.g. when Dashboard
  // increments chatKey to start a "New Chat").
  const isMountedRef = useRef(true);
  const abortControllerRef = useRef<AbortController | null>(null);

  // Mirror messages into a ref so sendChatMessage can read the latest history
  // without listing `messages` as a dependency. Stable identity prevents
  // re-rendering child components (suggested-prompt buttons, etc.) on every
  // message append.
  const messagesRef = useRef<ChatMessage[]>(messages);
  useEffect(() => {
    messagesRef.current = messages;
  }, [messages]);

  // Mirror onResponseReceived in a ref so callback identity stays stable even
  // when the parent passes a fresh function each render.
  const onResponseReceivedRef = useRef(onResponseReceived);
  useEffect(() => {
    onResponseReceivedRef.current = onResponseReceived;
  }, [onResponseReceived]);

  useEffect(() => {
    isMountedRef.current = true;
    // Pre-join the SignalR session group so real-time telemetry works from the first message
    joinTelemetrySession(sessionId);
    const unsubscribe = onProgress((data) => {
      if (data.sessionId === sessionId && isMountedRef.current) {
        setLoadingText(data.detail);
        const eventWithDuration = data as typeof data & { durationMs?: number };
        if (data.phase === 'tool_result') {
          setProgressSteps(prev => [...prev, {
            phase: data.phase,
            detail: data.detail,
            durationMs: eventWithDuration.durationMs,
            timestamp: data.timestamp,
          }]);
        }
      }
    });
    return () => {
      isMountedRef.current = false;
      abortControllerRef.current?.abort();
      unsubscribe();
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

      lastPromptRef.current = trimmed;
      setTimeoutInfo(null);

      setMessages(prev => [...prev, { role: 'user', content: trimmed }]);
      onResponseReceivedRef.current?.({ totalDurationMs: undefined });
      setLoading(true);
      setLoadingText('Thinking...');
      setProgressSteps([]);

      try {
        const history: ChatHistoryMessage[] = messagesRef.current
          .filter(m => m.role === 'user' || (m.role === 'assistant' && !m.content.startsWith('Error:')))
          .map(m => ({ role: m.role, content: m.content }));

        const options: SendMessageOptions = { signal: controller.signal };
        // Only include `forceExecutionPath` when the user has explicitly
        // picked a path AND the current auth mode supports the override.
        // Auto (the default) omits the field so the request payload for the
        // common case is byte-identical to the pre-#95 shape.
        const forceExecutionPath: ForceableExecutionPath | undefined =
          supportsExecutionPathOverride && forcePath !== 'auto' ? forcePath : undefined;
        const result = await sendMessage(
          {
            message: trimmed,
            sessionId,
            history,
            ...(forceExecutionPath ? { forceExecutionPath } : {}),
          },
          options,
        );
        if (!isMountedRef.current || controller.signal.aborted) return;

        // Plan review gate suspended the plan for reviewer input (#94/#96).
        // We surface the plan bubble immediately so the user can see the
        // proposed steps and drive the review. The reducer hydrates the
        // real plan detail (steps, statuses) from GET /api/plans/{id}.
        if (result.kind === 'suspended') {
          const suspended = result.suspended;
          if (planController) {
            void planController.startPlan({
              planId: suspended.planId,
              sessionId: suspended.sessionId,
              request: trimmed,
            });
          }
          setMessages(prev => [
            ...prev,
            {
              role: 'assistant' as const,
              content: suspended.message ?? 'Plan awaiting review.',
              planId: suspended.planId,
            },
          ]);
          return;
        }

        const response = result.response;
        // When the backend returns a 200 OK but the reply is actually an
        // error (rate-limit, timeout), suppress routing/telemetry metadata
        // so users don't see misleading "Agent X — 78% confidence" badges.
        const errorMasked = isErrorReply(response.reply);
        onResponseReceivedRef.current?.(errorMasked
          ? { totalDurationMs: response.totalDurationMs }
          : { totalDurationMs: response.totalDurationMs, tokenUsage: response.tokenUsage, routing: response.routing });
        // Sniff the reply for a Content Safety / guardrails refusal template.
        // When it matches, replace the raw reply with the whitelisted
        // `SafetyBlockDisplayModel` returned by `detectSafetyRefusal` so the
        // user sees a plain-language explanation without any internal
        // detection detail leaking through.
        const safetyDisplay = detectSafetyRefusal(response.reply);
        // Plan-first (unsuspended) 200 responses carry
        // routing.executionPath === 'plan'. Render the PlanView bubble
        // instead of the raw markdown so step statuses, per-step charts,
        // and the final aggregate reply all live in one surface.
        const isPlanResponse =
          response.routing?.executionPath === 'plan' && response.planId != null;
        if (isPlanResponse && response.planId) {
          const planId = response.planId;
          if (planController) {
            void planController.startPlan({
              planId,
              sessionId: response.sessionId,
              request: trimmed,
            });
          }
          setMessages(prev => [
            ...prev,
            {
              role: 'assistant' as const,
              content: response.reply,
              routing: response.routing,
              totalDurationMs: response.totalDurationMs,
              tokenUsage: response.tokenUsage,
              planId,
            },
          ]);
          return;
        }
        setMessages(prev => [
          ...prev,
          safetyDisplay
            ? {
                role: 'assistant' as const,
                content: safetyDisplay.reason,
                blocked: {
                  reason: safetyDisplay.reason,
                  suggestion: safetyDisplay.suggestion,
                  display: safetyDisplay,
                },
              }
            : errorMasked
              ? { role: 'assistant' as const, content: response.reply }
              : { role: 'assistant' as const, content: response.reply, spans: response.spans, charts: response.charts, routing: response.routing, totalDurationMs: response.totalDurationMs, tokenUsage: response.tokenUsage, memoryContext: response.memoryContext },
        ]);
      } catch (err) {
        if (!isMountedRef.current || controller.signal.aborted) return;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (isChatRequestTimeoutError(err)) {
          // Replace the hung-spinner behavior with a decision dialog. Do NOT
          // append an error bubble — the dialog is the visible feedback.
          setTimeoutInfo({ prompt: trimmed, timeoutMs: err.timeoutMs });
          return;
        }
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
    [sessionId, forcePath, supportsExecutionPathOverride, planController],
  );

  // User-initiated cancel while a run is in flight (issue #92). Aborts the
  // local fetch so the UI clears immediately, then asks the server to end
  // the run so tools stop consuming budget. Server response is treated as
  // fire-and-forget — the local abort already gave the user their signal.
  const handleCancel = useCallback(() => {
    if (!loading) return;
    const controller = abortControllerRef.current;
    controller?.abort();
    // Fire-and-forget: the local abort has already resolved the UI. A
    // 404 (nothing to cancel) is expected on the race where the run just
    // completed on the server; we don't surface it as an error.
    // Prefer the plan-scoped cancel when the current turn is a running
    // plan (#96 populated planController.active with a real planId).
    // Falls back to the session-scoped cancel for the fast/single-shot
    // path and for anonymous callers who cannot own a plan.
    const activePlan = planController?.active;
    const usePlanCancel =
      activePlan != null &&
      activePlan.planId.length > 0 &&
      isPlanRunning(activePlan.status);
    const cancelPromise = usePlanCancel
      ? cancelPlan(activePlan.planId)
      : cancelChatSession(sessionId);
    cancelPromise.catch((err) => {
      if (import.meta.env.DEV) console.error('Server-side cancel failed:', err);
    });
    if (isMountedRef.current) {
      setLoading(false);
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: 'Cancelled.' },
      ]);
    }
  }, [loading, sessionId, planController]);

  const handleTimeoutRetry = useCallback(() => {
    const prompt = timeoutInfo?.prompt ?? lastPromptRef.current;
    setTimeoutInfo(null);
    if (prompt) {
      void sendChatMessage(prompt);
    }
  }, [timeoutInfo, sendChatMessage]);

  const handleTimeoutAbandon = useCallback(() => {
    setTimeoutInfo(null);
    // Ensure any lingering abort controller is released so the UI is
    // fully editable again.
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    if (isMountedRef.current) {
      setLoading(false);
    }
  }, []);

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
    ? promptCategories.filter(c => c.id === selectedCategory)
    : promptCategories;

  const totalVisibleTasks = visiblePrompts.reduce((n, c) => n + c.tasks.length, 0);
  const hasAnyTasks = promptCategories.some((c) => c.tasks.length > 0);

  return (
    <div className={styles.panel}>
      <div className={styles.messages}>
        {messages.length === 0 && (
          <div className={styles.welcome}>
            <div className={styles.welcomeLogo}><BrandLogo size={56} /></div>
            <Text className={styles.welcomeText}>Welcome to Retail Pulse.</Text>
            <img
              className={styles.welcomeHeroLogo}
              src="/retail-pulse-logo-on-black.jpg"
              alt="Retail Pulse logo"
            />
            <Text className={styles.welcomeText}>
              Ask me about sales performance, inventory trends, or customer insights across your retail portfolio.
            </Text>
            <div className={styles.suggestedQueries}>
              {hasAnyTasks ? (
                <>
                  <div className={styles.categoryChips} role="group" aria-label="Suggested prompt categories">
                    <button
                      className={`${styles.categoryChip} ${selectedCategory === null ? styles.categoryChipActive : ''}`}
                      aria-pressed={selectedCategory === null}
                      onClick={() => setSelectedCategory(null)}
                    >
                      🏪 All
                    </button>
                    {promptCategories.map((cat) => (
                      <button
                        key={cat.id}
                        className={`${styles.categoryChip} ${selectedCategory === cat.id ? styles.categoryChipActive : ''}`}
                        aria-pressed={selectedCategory === cat.id}
                        onClick={() => setSelectedCategory(cat.id)}
                      >
                        {cat.emoji} {cat.label}
                      </button>
                    ))}
                  </div>
                  <div className={styles.promptGrid} data-testid="welcome-prompt-grid">
                    {totalVisibleTasks === 0 ? (
                      <div
                        className={styles.suggestedEmpty}
                        role="status"
                        data-testid="welcome-prompt-empty"
                      >
                        No starting tasks in this category.
                      </div>
                    ) : (
                      visiblePrompts.map((cat) =>
                        (selectedCategory ? cat.tasks : cat.tasks.slice(0, 1)).map((task, i) => (
                          <button
                            key={`${cat.id}-${i}`}
                            className={styles.suggestedQuery}
                            data-testid="welcome-prompt-item"
                            data-prompt-category={cat.id}
                            data-prompt-index={i}
                            data-prompt-text={task.prompt}
                            data-capability-kind={task.capability?.kind ?? ''}
                            data-capability-chart-type={task.capability?.chartType ?? ''}
                            data-capability-plan-path={task.capability?.planPath ?? ''}
                            aria-label={task.name}
                            onClick={() => handleSuggestedClick(task.prompt)}
                            disabled={loading}
                          >
                            <span>{cat.emoji}</span> {task.name}
                          </button>
                        ))
                      )
                    )}
                  </div>
                </>
              ) : (
                <div
                  className={styles.suggestedEmpty}
                  role="status"
                  data-testid="welcome-prompt-empty"
                >
                  <span aria-hidden="true">✨</span>
                  <Text>No starting tasks are defined for the active scenario.</Text>
                  <Text>Ask a question directly in the composer to get started.</Text>
                </div>
              )}
            </div>
          </div>
        )}

        {messages.map((msg, i) => (
          <div
            key={`msg-${msg.role}-${i}`}
            className={`${styles.message} ${msg.role === 'user' ? styles.messageUser : styles.messageAssistant}`}
            style={msg.planId ? { maxWidth: '95%' } : undefined}
          >
            <Avatar
              size={36}
              color={msg.role === 'user' ? 'colorful' : 'brand'}
              name={msg.role === 'user' ? 'User' : 'Retail Pulse'}
              icon={msg.role === 'user' ? undefined : ASSISTANT_AVATAR_ICON}
              style={msg.role === 'assistant' ? ASSISTANT_AVATAR_STYLE : undefined}
            />
            <div className={styles.messageContent}>
              {msg.role === 'assistant' && msg.planId && planController?.active?.planId === msg.planId ? (
                <PlanView
                  active={planController.active}
                  connected={planConnected ?? true}
                  onApprove={comment => { void planController.approve(comment); }}
                  onReject={feedback => { void planController.reject(feedback); }}
                  onEdit={editedSteps => { void planController.edit(editedSteps); }}
                  onClarify={answer => { void planController.clarify(answer); }}
                  onClose={() => planController.close()}
                />
              ) : (
              <Card
                className={`${styles.messageCard} ${msg.role === 'user' ? styles.userCard : styles.assistantCard}`}
                appearance="subtle"
              >
                {msg.role === 'assistant' && msg.blocked ? (
                  msg.blocked.display?.stage === 'output' ? (
                    <WithheldOutputMessage
                      display={msg.blocked.display}
                      suggestion={msg.blocked.suggestion}
                    />
                  ) : msg.blocked.display ? (
                    <BlockedRequestMessage display={msg.blocked.display} />
                  ) : (
                    <BlockedRequestMessage
                      reason={msg.blocked.reason}
                      suggestion={msg.blocked.suggestion}
                    />
                  )
                ) : msg.role === 'assistant' && msg.isStreaming ? (
                  <StreamingMessage
                    tokens={sanitizeMessage(msg.content)}
                    isStreaming={true}
                    onComplete={() => {
                      setMessages(prev => prev.map((m, idx) => idx === i ? { ...m, isStreaming: false } : m));
                    }}
                  />
                ) : msg.role === 'assistant' ? (
                  <div className="markdown-body">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>{sanitizeMessage(msg.content)}</ReactMarkdown>
                  </div>
                ) : (
                  <div>{msg.content}</div>
                )}
              </Card>
              )}
              {msg.role === 'assistant' && msg.planId && planController && planController.active?.planId !== msg.planId && (
                <Button
                  appearance="subtle"
                  onClick={() => { void planController.openHistoryPlan(msg.planId!); }}
                  data-testid="plan-reopen-button"
                  aria-label={`Reopen plan ${msg.planId}`}
                >
                  Reopen plan
                </Button>
              )}
              {msg.role === 'assistant' && msg.cacheInfo?.cached && (
                <CacheIndicator cacheInfo={msg.cacheInfo} />
              )}
              {msg.role === 'assistant' && msg.routing && !msg.planId && (
                <AgentRoutingIndicator routing={msg.routing} />
              )}
              {msg.role === 'assistant' && msg.memoryContext && msg.memoryContext.entries.length > 0 && (
                <MemoryIndicator memoryContext={msg.memoryContext} />
              )}
              {msg.approval && (
                <ApprovalCard approval={msg.approval} onResolved={onApprovalResolved} />
              )}
              {msg.spans && msg.spans.length > 0 && !msg.planId && (
                <SpansSummary spans={msg.spans} totalDurationMs={msg.totalDurationMs} tokenUsage={msg.tokenUsage} />
              )}
              {msg.charts && msg.charts.length > 0 && !msg.planId && (
                <ErrorBoundary fallback={<div className={styles.loadingContainer}>Chart failed to load.</div>}>
                  <Suspense fallback={<div className={styles.loadingContainer}><Spinner size="tiny" />Loading charts…</div>}>
                    <ChartRenderer charts={msg.charts} />
                  </Suspense>
                </ErrorBoundary>
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
              <ProgressIndicator
                currentPhase={loadingText}
                completedSteps={progressSteps}
              />
            </div>
          </div>
        )}

        {approvals && approvals.filter(a => a.status === 'pending').map(approval => (
          <div key={approval.id} className={`${styles.message} ${styles.messageAssistant}`} style={{ maxWidth: '95%' }}>
            <Avatar
              size={36}
              color="brand"
              name="Retail Pulse"
              icon={ASSISTANT_AVATAR_ICON}
              style={ASSISTANT_AVATAR_STYLE}
            />
            <div className={styles.messageContent}>
              <ApprovalCard approval={approval} onResolved={onApprovalResolved} />
            </div>
          </div>
        ))}

        <div ref={messagesEndRef} />
      </div>

      <div className={styles.inputArea}>
        <label htmlFor="chat-input" className="visually-hidden">
          Ask about retail performance
        </label>
        <ConnectionStatusIndicator status={hubStatus} stalled={hubStalled} />
        <PromptLibrary
          categories={promptCategories}
          onSelect={handleSuggestedClick}
          disabled={loading}
        />
        {supportsExecutionPathOverride && (
          <>
            <label htmlFor="execution-path-select" className="visually-hidden">
              Execution path
            </label>
            <select
              id="execution-path-select"
              className={`${styles.executionPathSelect} ${forcePath !== 'auto' ? styles.executionPathForced : ''}`}
              value={forcePath}
              onChange={(e) => setForcePath(e.target.value as 'auto' | ForceableExecutionPath)}
              disabled={loading}
              aria-label="Execution path"
              title={
                forcePath === 'auto'
                  ? 'Execution path: Auto — the router picks fast or plan for you.'
                  : forcePath === 'fast'
                    ? 'Execution path: Fast (forced) — single specialist, single shot.'
                    : 'Execution path: Plan (forced) — plan-first workflow with review when required.'
              }
              data-testid="execution-path-select"
            >
              <option value="auto">Auto</option>
              <option value="fast">Fast</option>
              <option value="plan">Plan</option>
            </select>
          </>
        )}
        <Input
          id="chat-input"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && handleSend()}
          placeholder="Ask about retail performance..."
          disabled={loading}
          style={FLEX_ONE_STYLE}
        />
        {loading ? (
          <Button
            appearance="secondary"
            onClick={handleCancel}
            aria-label="Cancel in-flight request"
            data-testid="chat-cancel-button"
          >
            Cancel
          </Button>
        ) : (
          <Button
            appearance="primary"
            icon={<Send24Regular style={{ color: '#ffffff' }} />}
            onClick={handleSend}
            disabled={!input.trim()}
            aria-label="Send message"
            style={SEND_BUTTON_STYLE}
          >
            Send
          </Button>
        )}
      </div>
      <TimeoutDialog
        open={timeoutInfo !== null}
        timeoutMs={timeoutInfo?.timeoutMs ?? 0}
        onRetry={handleTimeoutRetry}
        onAbandon={handleTimeoutAbandon}
      />
    </div>
  );
}
