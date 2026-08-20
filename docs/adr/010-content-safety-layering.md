# ADR-010: Optional Azure AI Content Safety second layer

## Status

Accepted (issue #100 — Azure AI Content Safety and Prompt Shields
integration).

## Context

Retail Pulse already ships a regex-based guardrails layer
(`GuardrailPatterns`, `JailbreakDetector`, `PiiRedactor`, `GuardrailsMiddleware`).
That layer is fast, deterministic, and has zero cloud dependencies — which are
the same properties that make it the default. It is also easy to evade with
multilingual, obfuscated, or long-context payloads that a strict regex list
cannot practically enumerate.

We want an optional second layer that:

* Adds Azure AI Content Safety (text moderation with per-category severity
  thresholds) and Prompt Shields (jailbreak + indirect-injection detection).
* Covers all four Retail Pulse trust boundaries: user input, model output,
  retrieved knowledge chunks (RAG), and tool results.
* Ships **disabled by default**. With no Content Safety resource, the
  solution must build, start, and pass the full test suite with behaviour
  byte-for-byte identical to today's regex-only guardrails.
* Never requires an API key in configuration — the API authenticates with a
  managed identity via `DefaultAzureCredential`.
* Has an explicit fail-open vs fail-closed policy for the enabled-but-
  unreachable case, and audits both outcomes so an operator can distinguish
  a real block from a policy decision.

## Decision

Introduce `IContentSafetyEvaluator` (Retail Pulse-side abstraction) with two
implementations:

* `NoOpContentSafetyEvaluator` — always returns `Passed`. Registered whenever
  the layer is disabled. No `ContentSafetyClient` or `DefaultAzureCredential`
  is constructed on the disabled path, so a missing endpoint cannot break
  startup.
* `AzureContentSafetyEvaluator` — thin wrapper over `ContentSafetyClient`
  (from `Azure.AI.ContentSafety` 1.0.0) for text moderation, plus a raw
  `HttpClient` call to `POST /contentsafety/text:shieldPrompt` for Prompt
  Shields (the 1.0.0 SDK does not yet expose a typed method for that
  operation). Uses a bounded per-call timeout and a shared resilience
  pipeline (`AddContentSafetyResilienceHandler`) that mirrors the MCP
  circuit-breaker settings (5 failures / 30 s break / 30 s sampling) and
  reports state to `CircuitBreakerHealthCheck` so the existing readiness
  probe surfaces it.

The regex layer runs first at every seam. Only when it passes does the
middleware / RAG / tool-result path call the evaluator. The design deliberately
preserves the regex layer's short-circuit semantics because it is the layer
that guarantees the disabled-parity contract.

### Layering diagram (input path)

```
ChatRequest -> [length gate] -> [jailbreak regex] -> [SQL/XSS regex]
            -> [access control] -> [PII scan (optional)] -> [Content Safety]
            -> agent pipeline
```

Output path: PII redaction runs first (regex-based, cheap, tenant-scoped),
then Content Safety moderates the redacted text so no raw PII ever leaves the
process boundary as part of a moderation call.

### Tool-result seam

Tool results already flow through `Budget/BudgetedAIFunction`, which is the
outermost wrapper for every `AIFunction`. That wrapper is registered outside
`src/RetailPulse.Api/Agents/` and every tool invocation must pass through it
to hit the `ToolResultBudget`. It is the correct seam for tool-result
moderation.

To keep the wiring change tightly scoped, the inspector is exposed to
`BudgetedAIFunction` via a static ambient accessor
(`ContentSafetyToolResultAmbient.Install(...)` called once from `Program.cs`).
No constructor of any type under `src/RetailPulse.Api/Agents/` changes; the
per-agent code and the agent-execution pipeline remain untouched by this
issue. Agent-specific instrumentation, if we want it, is a follow-up
alongside issue #89 (which owns that tree).

### RAG seam

`RagContextProvider` evaluates every surviving chunk (after the BM25 score
cut) individually so a single poisoned chunk cannot silently pull a benign
neighbour with it. Prompt Shields is invoked in *document* mode on that path,
which is what the service uses for indirect-injection detection. Dropped
chunks are audited with `content-safety-indirect-injection` (or the
moderation category that fired) so operators can distinguish a benign
relevance miss from a safety-driven drop.

### Fail-open vs fail-closed

`ContentSafetyConfig.OnUnavailable` is an explicit enum
(`FailOpen` | `FailClosed`). The default is `FailOpen`, which matches the
"optional second layer" positioning — a regulated deployment should set
`FailClosed`. Every outcome is audited:

| Decision            | Action recorded    | Effect                                          |
| ------------------- | ------------------ | ----------------------------------------------- |
| Blocked (input)     | `blocked`          | Request refused; refusal template rendered.     |
| Blocked (output)    | `blocked`          | Response substituted with the refusal template. |
| Blocked (RAG)       | `dropped`          | Chunk excluded from the context.                |
| Blocked (tool)      | `blocked`          | Tool result replaced with a diagnostic envelope.|
| Flagged             | `flagged`          | Audited only; caller not blocked.               |
| ServiceUnavailable  | `failopen-passed`  | Fail-open policy: request continues.            |
| ServiceUnavailable  | `failclosed-blocked` | Fail-closed policy: request refused.          |

The existing `/api/guardrails/suspicious` audit feed and the guardrails
dashboard surface these rows unchanged — `content-safety-*` is a new family
of `DetectionType` values, not a new schema.

### RBAC and configuration

No key material appears in configuration. `ContentSafetyConfig` intentionally
has no `ApiKey` / `Key` / `SecretKey` member; a reflection-based contract
test enforces this so a future contributor cannot regress it. The Bicep
module (`infra/modules/content-safety.bicep`) sets `disableLocalAuth = true`
on the Cognitive Services account and the postprovision hook grants the
container-app system identities the `Cognitive Services User` role
idempotently. The `/api/guardrails/config` endpoint deliberately does not
expose the endpoint URL — only the flags an operator can toggle at runtime.

### Telemetry

Every evaluator call emits a `guardrails.contentsafety.{input|output|
retrieved_knowledge|tool_result}` `Activity` on the same `AgentTelemetry`
`ActivitySource` used by the rest of the middleware, so existing OpenTelemetry
exporters and traces pick it up with no extra plumbing. The tags include
`decision`, `latency_ms`, `prompt_shield.jailbreak`,
`prompt_shield.indirect`, and a `categories` list — decisions and category
names only, never the payload.

## Consequences

* The disabled default keeps `dotnet run --project src/RetailPulse.AppHost`
  and the full test suite unchanged.
* Enabling the layer costs one round-trip per stage to a regional Content
  Safety endpoint. The bounded timeout + circuit breaker prevents a bad
  region from cascading into an outage.
* Prompt Shields is language-tuned for English at GA; other languages fall
  back to text-moderation categories. Regulated deployments should combine
  `FailClosed` with `PromptShieldsEnabled = true` and expect false positives
  on multilingual traffic — the `flagged` audit rows help calibrate.
* The tool-result seam via the ambient accessor is deliberately scoped so a
  future contributor cannot forget to wire a new agent for moderation —
  every tool call already passes through `BudgetedAIFunction`. Once #89
  lands, richer agent-pipeline integration (per-tool policies, per-agent
  overrides) can build on this seam.
