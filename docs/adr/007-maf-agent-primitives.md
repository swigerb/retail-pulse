# ADR-007: MAF agent primitives for specialist / router / council execution

## Status

Accepted

## Context

Retail Pulse had three distinct LLM invocation surfaces:

1. `AgentExecutionPipeline.ExecuteAsync` / `ExecuteWithProgressAsync` — the
   shared execution path all 10 specialists route through.
2. `RetailOpsRouter.ClassifyIntentAsync` — LLM-based intent classification when
   the keyword fast-path misses.
3. `ConsensusOrchestrator.CollectVoteAsync` and `SynthesizeVerdictAsync` — the
   Portfolio Health Council per-agent votes and the final synthesis pass.

All three called `IChatClient.GetResponseAsync` directly. Central packages already
pin the Microsoft Agent Framework (MAF) 1.18.0 stack (`Microsoft.Agents.AI` +
`Microsoft.Agents.AI.Abstractions` + `Microsoft.Agents.AI.OpenAI` +
`Microsoft.Agents.AI.Workflows`), but `AIAgent` / `ChatClientAgent` were only
referenced ceremonially: no execution path actually invoked a MAF agent
primitive. GitHub issue #89 called this out as a durable structural gap — the
"agent" name in the codebase did not correspond to a MAF agent object at
runtime, which blocks future adoption of the workflow, orchestration, and
session APIs the framework offers.

Two operational constraints made this non-trivial to fix:

- **The decorator stack is load-bearing.** `Program.cs` composes the production
  `IChatClient` with `UseFunctionInvocation(client =>
  client.MaximumIterationsPerRequest = 3)` (ADR-006), `UseOpenTelemetry(...)`,
  the MCP HTTP retry / circuit-breaker / cache handlers, and the
  anonymous-output cap. If a naive migration lets MAF add its own
  `FunctionInvokingChatClient`, that cap silently doubles (or worse), spans
  duplicate, and MCP resilience shifts one layer out.
- **Downstream helpers depend on `Microsoft.Extensions.AI.ChatResponse`.** The
  chart extractor, tool-span recorder, token-cost calculator, and
  ADR-006 budget scope all read `response.Messages`, `response.Usage`, and
  `response.Text` off the `Microsoft.Extensions.AI` type. Changing every helper
  signature would be a large blast radius with no user-visible benefit.

## Decision

Introduce a single, static invocation seam,
`RetailPulse.Api.Agents.MafAgentInvoker.RunAsync`, that every specialist,
router, and council call site now flows through. The adapter:

1. Constructs a per-invocation `ChatClientAgent(chatClient, options,
   loggerFactory?)` with `ChatClientAgentOptions.Name` set to a stable
   per-caller identifier (`"{AgentName}.specialist"` for specialists,
   `"RetailOpsRouter.classifier"` for the router,
   `"ConsensusOrchestrator.voter.{agentKey}"` and
   `"ConsensusOrchestrator.synthesizer"` for the council).
2. Sets `ChatClientAgentOptions.UseProvidedChatClientAsIs = true`. This is the
   critical flag — it tells MAF **not** to re-decorate the incoming
   `IChatClient`. The composition-root decorator stack (function-invocation with
   ADR-006's `MaximumIterationsPerRequest = 3`, OpenTelemetry, MCP resilience,
   caching, anonymous-output cap) is preserved end-to-end.
3. Wraps the caller's `ChatOptions` in a `ChatClientAgentRunOptions(chatOptions)`
   so temperature, tool list, response format, and max-output-tokens reach the
   chat client unchanged.
4. Invokes `ChatClientAgent.RunAsync(messages, session: null, runOptions, ct)`.
   `session: null` yields an ephemeral in-memory MAF session per invocation —
   Retail Pulse constructs the entire transcript (system prompt + trimmed
   history + user message) on every call, so cross-request session persistence
   is neither expected nor desired.
5. Returns the real `Microsoft.Agents.AI.AgentResponse`. Callers convert to
   `Microsoft.Extensions.AI.ChatResponse` through the documented shallow-copy
   `AgentResponseExtensions.AsChatResponse()`, which shares the same
   `Messages`/`Usage` list references — allocation-cheap and semantically
   identical for every downstream helper (chart extraction, tool-span recording,
   token cost, budget bookkeeping).

`ISpecialistAgent`, `IAgentRouter`, and `IConsensusCouncil` remain unchanged;
the migration is internal to the three implementations.

## Consequences

**Positive**

- The three execution surfaces now genuinely run through a MAF `ChatClientAgent`
  primitive, not just through symbols. Future adoption of `AgentSession`
  persistence, MAF `WorkflowBuilder` orchestration, or the runtime memory APIs
  only needs changes inside `MafAgentInvoker`, not scattered across the
  specialists / router / council.
- ADR-006 iteration cap, OpenTelemetry span sequence
  (`agent.thought` → `agent.response`, tool spans nested inside), MCP HTTP
  retry/circuit-breaker/cache, ADR-006 tool-context budget, all 9 chart types,
  `InstrumentedToolMiddleware` timings, `StreamingProgressFeature`, blocking
  `ApprovalTool`, `RequestToolContext` `AsyncLocal` scope, anonymous output cap,
  cancellation/timeout paths, and per-model token cost all continue to work
  byte-for-byte, verified by the pre-existing regression tests plus the new
  `MafPrimitivesCharacterizationTests`.
- A stack-frame based characterization test injects a probe `IChatClient` and
  asserts a `Microsoft.Agents.AI.*` frame appears on every call. That means a
  future refactor that quietly bypasses MAF and calls `IChatClient` directly
  will fail the build.

**Negative / neutral**

- One additional allocation per LLM invocation (the `ChatClientAgent` +
  `ChatClientAgentRunOptions` pair). These objects are small and per-request,
  and MAF stores no expensive session state when constructed with
  `session: null` and `UseProvidedChatClientAsIs = true`.
- MAF's own emitted OpenTelemetry spans overlap with the existing
  `RetailPulse.Agent` `agent.thought` / `agent.response` scopes. Because
  `UseProvidedChatClientAsIs = true` prevents MAF from re-decorating the client,
  MAF only adds a thin outer `ChatClientAgent.RunAsync` scope; no duplication of
  the ADR-006 iteration cap or of tool-call spans occurs. Downstream span
  parsers key on the operation names Retail Pulse already emits.

## Alternatives considered

- **Rename types only.** Import `AgentResponse` as a type alias and keep calling
  `IChatClient.GetResponseAsync`. Rejected: this satisfies grep but not the
  intent of issue #89 — no MAF primitive is on the execution path.
- **Let MAF re-decorate the `IChatClient`.** Simpler code, but MAF would install
  its own `FunctionInvokingChatClient` on top of ours, overriding
  `MaximumIterationsPerRequest = 3` (ADR-006), duplicating OpenTelemetry spans,
  and moving the MCP resilience decorators one layer away from the transport.
  Rejected as it silently breaks contracts the existing regression suite
  guards.
- **Materialise a durable `AgentSession` per user session.** Requires a
  cross-request session cache with its own lifetime and TPM implications, and
  Retail Pulse already carries the full transcript on every request. Deferred:
  MAF's session APIs can be adopted incrementally inside `MafAgentInvoker`
  without any change to the three call sites.

## References

- Issue [#89 — Migrate agent execution onto real MAF agent primitives](https://github.com/swigerb/retail-pulse/issues/89)
- [ADR-006: Tool-Context Budget](./006-tool-context-budget.md) — the iteration
  cap preserved by `UseProvidedChatClientAsIs = true`.
- `RetailPulse.Api/Agents/MafAgentInvoker.cs`
- `RetailPulse.Tests/Agents/MafPrimitivesCharacterizationTests.cs`
