### Quotas & Bounded Storage for In-Memory Stores (2026-05-13)

- **Context:** Code review flagged unbounded in-memory stores as DoS vectors — knowledge base accepted unlimited uploads, cost tracker grew without limit, conversation exporter had no session/message caps and used thread-unsafe `List<T>` for concurrent writes.
- **Decision:** All three in-memory stores now enforce configurable quotas via `IOptions<T>` pattern:
  - **Knowledge base:** 10MB per doc, 100 docs max, 5000 chunks max (all configurable under `Knowledge:` config section)
  - **Cost tracker:** 10K events max + 24h TTL eviction on write, ConcurrentQueue for FIFO eviction (configurable under `Observability:` section)
  - **Conversation exporter:** 1K sessions with LRU eviction, 200 messages/session, lock-per-session for thread safety (configurable under `Observability:` section)
  - Options classes: `KnowledgeOptions` and `ObservabilityOptions` in `src/RetailPulse.Api/Configuration/`
- **Key design choices:**
  1. ConcurrentQueue over ConcurrentBag for cost tracker — preserves insertion order for TTL eviction
  2. Lock-per-session over ConcurrentBag for message lists — preserves message order in exports
  3. LRU via `Volatile.Read/Write` on a `long LastActivity` ticks field — avoids heavy locking for activity tracking
  4. Validation happens *before* ingestion/storage, not after — fail fast with clear error messages
- **Impact:** All in-memory stores are now bounded and thread-safe. Quotas are runtime-configurable via appsettings. No contract changes needed.
- **Owner:** Costco (Backend Dev)
