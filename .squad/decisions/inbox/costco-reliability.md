# Sprint 3 Reliability Improvements (2026-05-13)

## s3-async-sqlite — Async SQLite Approval Store

- **Context:** `SqliteApprovalGate` used synchronous `Open`/`ExecuteNonQuery`/`ExecuteReader` calls wrapped in `Task.FromResult`, blocking thread pool threads on I/O. `WaitForApprovalAsync` used a fixed 2-second polling interval.
- **Decision:** Converted all SQLite operations to async APIs (`OpenAsync`, `ExecuteNonQueryAsync`, `ExecuteReaderAsync`) with `CancellationToken` propagation. Replaced fixed-interval polling in `WaitForApprovalAsync` with exponential backoff (250ms initial, 2x multiplier, 4s max).
- **Impact:** Thread pool threads are no longer blocked on SQLite I/O. Cancellation is respected throughout. Backoff reduces database polling frequency for long-running approvals.
- **Owner:** Costco (Backend Dev)

## s3-bounded-queues — Bounded Channel Memory Extraction

- **Context:** Chat endpoints used `Task.Run` fire-and-forget for memory extraction, with no backpressure, no cancellation propagation, and no way to observe dropped work.
- **Decision:** Introduced `MemoryExtractionChannel` (bounded `Channel<MemoryWorkItem>`, capacity 1000) and `MemoryExtractionBackgroundService` (hosted service) to process memory extraction asynchronously. The channel uses `BoundedChannelFullMode.Wait` with `TryWrite` — when full, writes return false and a `DroppedCount` counter increments for observability. Background service uses linked cancellation tokens with 30s per-item timeout.
- **Impact:** Memory extraction has backpressure protection, observability via `DroppedCount`, and clean shutdown. Chat endpoints are no longer responsible for exception handling of background work.
- **Owner:** Costco (Backend Dev)

## s3-bot-health — Bot Health with SignalR Monitoring

- **Context:** `TelemetrySignalRClient` had no retry logic — a single connection failure at startup silently disabled telemetry. No health reporting for SignalR connectivity.
- **Decision:** Added exponential backoff reconnection (1s initial, 2x multiplier, 30s max, 10 attempts). Made health mode configurable via `TeamsBot:HealthMode` setting ("fail-fast" throws on exhausted retries, "degraded" continues without telemetry). Added `SignalRHealthCheck` IHealthCheck that reports Healthy/Degraded/Unhealthy based on connection state and mode. Wired `Closed`/`Reconnected` events for live state tracking.
- **Impact:** Bot remains functional when SignalR is unavailable (degraded mode). Health endpoints expose SignalR connectivity. Fail-fast mode available for environments where telemetry is required.
- **Owner:** Costco (Backend Dev)

## s3-signalr-backpressure — SignalR Telemetry Backpressure

- **Context:** `InMemoryTraceCollector.CaptureSpan` used `Task.Run` and fire-and-forget `_ =` for SignalR push notifications, with no backpressure and silently swallowed exceptions.
- **Decision:** Introduced `TelemetryPushChannel` (bounded channel, capacity 1000) and `TelemetryPushBackgroundService`. Trace collector now writes to the channel instead of spawning tasks. Dropped telemetry count is tracked via `DroppedCount` on the channel. Background service uses linked cancellation with 5s per-item timeout.
- **Impact:** SignalR pushes are rate-limited by channel capacity. Dropped telemetry is observable. No more unbounded task spawning from hot-path span capture.
- **Owner:** Costco (Backend Dev)

## s3-memory-cancellation — Memory Cancellation + Logged Exceptions

- **Context:** Streaming chat endpoint (`/api/chat/stream`) used `Task.Run` with `CancellationToken.None` for memory extraction and caught exceptions with `catch { /* swallow */ }`, hiding failures.
- **Decision:** Both chat endpoints now enqueue memory work via `MemoryExtractionChannel.TryWrite` instead of `Task.Run`. The background service uses linked cancellation tokens. The streaming endpoint no longer swallows exceptions — failures are logged by the background service.
- **Impact:** Memory extraction respects application shutdown. Failures are visible in logs. No fire-and-forget tasks in endpoint handlers.
- **Owner:** Costco (Backend Dev)
