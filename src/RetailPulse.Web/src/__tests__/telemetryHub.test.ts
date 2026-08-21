import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

// Mock @microsoft/signalr with a controllable stub so we can drive status
// transitions deterministically without opening a real socket. This is
// enough to prove the connect-status pipeline, the reconnect-rejoin path,
// the heartbeat listener, and the terminal transition on onclose.
type Handler<T = unknown> = (arg?: T) => void;

interface StubConnection {
  state: number;
  __handlers: {
    reconnecting?: Handler<Error>;
    reconnected?: Handler<string>;
    close?: Handler<Error>;
    events: Map<string, Set<Handler<unknown>>>;
  };
  __start: { resolve: () => void; reject: (err: Error) => void; promise: Promise<void> };
  __invokeCalls: Array<[string, ...unknown[]]>;
  __retryPolicy?: { nextRetryDelayInMilliseconds: (ctx: unknown) => number | null };
  onreconnecting: (cb: Handler<Error>) => void;
  onreconnected: (cb: Handler<string>) => void;
  onclose: (cb: Handler<Error>) => void;
  on: (event: string, cb: Handler<unknown>) => void;
  off: (event: string, cb?: Handler<unknown>) => void;
  start: () => Promise<void>;
  stop: () => Promise<void>;
  invoke: (name: string, ...args: unknown[]) => Promise<void>;
  __emit: (event: string, payload?: unknown) => void;
}

// `vi.mock('@microsoft/signalr', ...)` is hoisted above this module-level
// `const`, so referencing `HubConnectionState` directly on the factory's
// returned object (below) would hit its temporal dead zone when
// telemetryHub.ts imports @microsoft/signalr during the hoisted `import`
// on line ~124. `vi.hoisted` runs alongside the hoisted mocks so the
// binding exists before the factory evaluates.
const { HubConnectionState } = vi.hoisted(() => ({
  HubConnectionState: {
    Disconnected: 0,
    Connecting: 1,
    Connected: 2,
    Reconnecting: 3,
    Disconnecting: 4,
  } as const,
}));

function createStubConnection(): StubConnection {
  let resolveStart!: () => void;
  let rejectStart!: (err: Error) => void;
  const startPromise = new Promise<void>((resolve, reject) => {
    resolveStart = resolve;
    rejectStart = reject;
  });

  const events = new Map<string, Set<Handler<unknown>>>();
  const invokeCalls: Array<[string, ...unknown[]]> = [];

  const conn: StubConnection = {
    state: HubConnectionState.Disconnected,
    __handlers: { events },
    __start: { resolve: resolveStart, reject: rejectStart, promise: startPromise },
    __invokeCalls: invokeCalls,
    onreconnecting(cb) {
      conn.__handlers.reconnecting = cb as Handler<Error>;
    },
    onreconnected(cb) {
      conn.__handlers.reconnected = cb as Handler<string>;
    },
    onclose(cb) {
      conn.__handlers.close = cb as Handler<Error>;
    },
    on(event, cb) {
      let set = events.get(event);
      if (!set) {
        set = new Set();
        events.set(event, set);
      }
      set.add(cb);
    },
    off(event, cb) {
      if (!cb) {
        events.delete(event);
        return;
      }
      events.get(event)?.delete(cb);
    },
    async start() {
      conn.state = HubConnectionState.Connected;
      return startPromise;
    },
    async stop() {
      conn.state = HubConnectionState.Disconnected;
      // If a test never explicitly resolved __start, reject the pending
      // start now so the `connection.start().then(...).catch(...)` chain
      // inside connectTelemetryHub settles. Otherwise
      // disconnectTelemetryHub awaits a promise that never resolves and
      // hangs the afterEach hook until vitest's 10s hook timeout fires,
      // which is then reported against the next test's beforeEach.
      rejectStart(new Error('stub connection stopped before start settled'));
    },
    async invoke(name, ...args) {
      invokeCalls.push([name, ...args]);
    },
    __emit(event, payload) {
      const set = events.get(event);
      if (!set) return;
      set.forEach((cb) => cb(payload));
    },
  };

  return conn;
}

let currentStub: StubConnection | null = null;

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private policy?: { nextRetryDelayInMilliseconds: (ctx: unknown) => number | null };
    withUrl() { return this; }
    withAutomaticReconnect(policy?: { nextRetryDelayInMilliseconds: (ctx: unknown) => number | null }) {
      this.policy = policy;
      return this;
    }
    build() {
      const conn = createStubConnection();
      conn.__retryPolicy = this.policy;
      currentStub = conn;
      return conn;
    }
  }

  return {
    HubConnectionBuilder,
    HubConnectionState,
  };
});

// Import AFTER vi.mock so telemetryHub picks up the stub.
import {
  connectTelemetryHub,
  joinTelemetrySession,
  disconnectTelemetryHub,
  onHubConnectionStatus,
  onHubHeartbeat,
  getHubConnectionStatus,
  __resetTelemetryHubForTests,
  type HubConnectionStatus,
} from '../services/telemetryHub';
import { DEFAULT_RECONNECT_BACKOFF } from '../services/reconnectBackoff';

function waitForStart(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('telemetryHub status + reconnect wiring (issue #92)', () => {
  beforeEach(() => {
    currentStub = null;
    __resetTelemetryHubForTests();
  });

  afterEach(async () => {
    // Deterministic cleanup: any test that called connectTelemetryHub but
    // never resolved the stub's start promise would leave the chained
    // module-level startPromise pending. disconnectTelemetryHub awaits
    // that startPromise before calling stop(), so a pending stub start
    // would hang the hook until the 10s hook timeout fires and get
    // reported against the NEXT test's beforeEach. Resolving the pending
    // start here settles the chain synchronously in the microtask queue.
    currentStub?.__start.resolve();
    try {
      await disconnectTelemetryHub();
    } finally {
      __resetTelemetryHubForTests();
      // Guarantee real timers regardless of whether any test in this
      // file (now or later) turned on fake timers and threw before
      // restoring them. Cheap when timers are already real.
      vi.useRealTimers();
    }
  });

  it('emits connecting → connected transitions to subscribers', async () => {
    const seen: HubConnectionStatus[] = [];
    onHubConnectionStatus((s) => { seen.push(s); });

    connectTelemetryHub(() => {}, undefined, undefined);
    // Resolve the pending start promise so the .then() branch fires.
    currentStub!.__start.resolve();
    await waitForStart();

    // Subscribers see the initial disconnected value (from before connect),
    // then connecting, then connected.
    expect(seen).toContain('connecting');
    expect(seen[seen.length - 1]).toBe('connected');
    expect(getHubConnectionStatus()).toBe('connected');
  });

  it('installs an IRetryPolicy backed by the default backoff schedule', async () => {
    connectTelemetryHub(() => {});
    // Match the lifecycle used by every other test in this file so the
    // module-level startPromise chained inside connectTelemetryHub
    // settles and afterEach can tear down cleanly.
    currentStub!.__start.resolve();
    await waitForStart();
    expect(currentStub?.__retryPolicy).toBeDefined();

    const policy = currentStub!.__retryPolicy!;
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 0, elapsedMilliseconds: 0, retryReason: new Error() })).toBe(DEFAULT_RECONNECT_BACKOFF.initialDelayMs);
    // Retry budget must be terminal at maxAttempts.
    expect(policy.nextRetryDelayInMilliseconds({
      previousRetryCount: DEFAULT_RECONNECT_BACKOFF.maxAttempts,
      elapsedMilliseconds: 0,
      retryReason: new Error(),
    })).toBeNull();
  });

  it('surfaces reconnecting → connected and re-joins active sessions after reconnect', async () => {
    const seen: HubConnectionStatus[] = [];
    onHubConnectionStatus((s) => { seen.push(s); });

    connectTelemetryHub(() => {});
    currentStub!.__start.resolve();
    await waitForStart();

    await joinTelemetrySession('sess-mine');
    expect(currentStub!.__invokeCalls.find(c => c[0] === 'JoinSession' && c[1] === 'sess-mine')).toBeDefined();

    // Clear invoke history and drive a reconnect cycle.
    currentStub!.__invokeCalls.length = 0;
    currentStub!.__handlers.reconnecting?.(new Error('transport dropped'));
    currentStub!.__handlers.reconnected?.('new-connection-id');

    expect(seen).toContain('reconnecting');
    expect(seen[seen.length - 1]).toBe('connected');

    // The rejoin MUST use only the client-tracked session id, not anything
    // provided by server payloads (the reconnected callback receives a
    // connection id string, which we deliberately ignore).
    const rejoinCalls = currentStub!.__invokeCalls.filter(c => c[0] === 'JoinSession');
    expect(rejoinCalls).toEqual([['JoinSession', 'sess-mine']]);
  });

  it('transitions to the terminal disconnected state when the connection closes', async () => {
    const seen: HubConnectionStatus[] = [];
    onHubConnectionStatus((s) => { seen.push(s); });

    connectTelemetryHub(() => {});
    currentStub!.__start.resolve();
    await waitForStart();

    currentStub!.__handlers.close?.(new Error('retry budget exhausted'));

    expect(seen[seen.length - 1]).toBe('disconnected');
    expect(getHubConnectionStatus()).toBe('disconnected');
  });

  it('delivers heartbeat events to subscribers', async () => {
    const heartbeats: unknown[] = [];
    onHubHeartbeat((e) => { heartbeats.push(e); });

    connectTelemetryHub(() => {});
    currentStub!.__start.resolve();
    await waitForStart();

    currentStub!.__emit('heartbeat', { timestamp: '2026-08-20T00:00:00Z', intervalMs: 15000 });
    currentStub!.__emit('heartbeat', { timestamp: '2026-08-20T00:00:15Z', intervalMs: 15000 });

    expect(heartbeats).toHaveLength(2);
    expect(heartbeats[0]).toMatchObject({ intervalMs: 15000 });
  });

  it('ignores duplicate joinTelemetrySession calls for the same id', async () => {
    connectTelemetryHub(() => {});
    currentStub!.__start.resolve();
    await waitForStart();

    await joinTelemetrySession('sess-dup');
    await joinTelemetrySession('sess-dup');
    await joinTelemetrySession('sess-dup');

    const joinCalls = currentStub!.__invokeCalls.filter(c => c[0] === 'JoinSession' && c[1] === 'sess-dup');
    expect(joinCalls).toHaveLength(1);
  });
});
