# ADR-011: Load-time safety validation of agent and tenant configuration

## Status

Accepted (issue #99 — Safety validation of agent and tenant configuration at
load time).

## Context

Retail Pulse loads `prompts.yaml` at host startup, hydrates it into a
`PromptConfiguration`, and then constructs one Semantic Kernel agent per
entry with the tools its definition lists. ADR-008 established that
`prompts.yaml` is a **trusted-at-load-time** deployment artifact: it ships
in the container image and is authored by the same team that authors code.

That trust posture is correct, but "trusted at load time" is not the same
as "correct at load time". A hostile prompt that slipped past code review,
a mis-typed model name, a definition that grants itself a write tool it
should never hold, or a copy-paste with `ignore previous instructions`
embedded in an example — any of those would previously be caught, at best,
at runtime by the request-side guardrails middleware (issue #98) or the
Content Safety second pass (ADR-010 / issue #100), and only for the
specific request that happened to reach the middleware. Definitions with
`Temperature = 2.0`, models that were never provisioned, or duplicate agent
names would happily construct.

We want the loader itself to reject a bad definition **before any agent is
constructed**, using the same building blocks as the request-side path so
we don't ship a second, drift-prone safety implementation.

## Decision

Introduce `AgentDefinitionValidator` in
`RetailPulse.Api.Guardrails.AgentDefinition`, invoked from `Program.cs`
after the `PromptConfiguration` is hydrated and before `AddAgentRouting`.
The validator layers four checks against every agent definition:

1. **Structural.** Required fields, temperature in
   `Guardrails:AgentDefinition:Temperature` bounds, model in
   `AllowedModels`, tool names in `AllowedTools`, no duplicate agent keys,
   `SystemPrompt` under `MaxSystemPromptLength`.
2. **Policy.** Every tool a definition names must exist in the runtime
   `AgentToolRegistry` (checked through the load-time
   `AgentDefinitionValidatorToolCatalog` shim so we don't need scoped DI at
   startup). Privileged write tools require an explicit
   `Guardrails:AgentDefinition:PrivilegedTools` grant naming the allowed
   agent keys. `RequestApproval` is the first such tool; `UpdateMetrics`
   and any future write tool will opt into the same grant list.
3. **Pattern layer.** System prompts and descriptions are run through the
   existing `GuardrailPatterns` / `JailbreakDetector` regex layer first.
   That's the cheapest check and it does not depend on Azure.
4. **Content Safety.** Text that survives the pattern layer is sent through
   `IContentSafetyEvaluator` on a new `ContentSafetyStage.AgentDefinition`
   stage. Prompt-shield jailbreak and indirect-injection verdicts count as
   rejections. Text already blocked by the pattern layer is not
   re-evaluated — no double-billing, no leak of pattern-caught payloads to
   the second service.

### Failure policies

`Guardrails:AgentDefinition:OnValidationFailure` has two values:

* `RefuseStartup` (default) — collect every violation across every agent,
  then throw a single `AgentDefinitionValidationException`. The container
  refuses to serve, which is the safer default when we can't tell whether
  the drift is malicious.
* `QuarantineOffender` — remove offending agent keys from
  `PromptConfiguration.Agents`, emit exactly one
  `LogWarning("Quarantined agent definition {AgentKey}: {ReasonSummary}",
  ...)` per removed key, and continue startup with the surviving roster.
  Useful for staged rollouts where refusing the whole deploy is worse than
  temporarily losing one agent.

There is no third "silent accept" option. Every code path either throws or
quarantines.

### Audit

Every rejection writes a `SuspiciousRequest` row through the same
`ISuspiciousRequestLog` used at runtime, wired as a shared singleton so
load-time and request-time events land in the same durable feed. Rows
carry `UserContext = "startup-validator"`, an `agent-definition-*`
`DetectionType`, an `Action` of `blocked`, `quarantined`, or
`failopen-passed`, and a message that names the agent, field, and rule id.
**The raw offending prompt text is never included** in the row or in log
lines — that would leak the payload we're trying to reject into the audit
trail.

`agent-definition-content-safety-unavailable` is a distinct detection
type: it is emitted whenever the evaluator returns `ServiceUnavailable`,
with `Action = failopen-passed` when the configured policy accepts the
definition anyway, and `Action = blocked` when it rejects. Operators can
filter for exactly the events that were let through only because Content
Safety was down.

### Content-Safety-disabled path

The gate does not have a hard dependency on Azure Content Safety.
`Guardrails:ContentSafety:Enabled = false` (or
`Guardrails:AgentDefinition:SafetyChecksEnabled = false`) skips only the
fourth layer; structural, policy, and pattern checks still run. The
disabled path is documented as pattern-only in `docs/security.md`: a
plain-text `ignore previous instructions` still rejects, but arbitrary
encoded payloads (base64, homoglyph, multilingual) that need a model to
decode will pass. That's the honest limit and it's covered by
`AgentDefinitionValidatorContentSafetyDisabledTests`.

### Public projection

`/api/guardrails/config` exposes the failure policy, the safety toggle,
and the temperature bounds — everything operators need to see the gate's
configured behaviour. The deployment allow-lists (models, tools) and the
privileged-tool grants are **not** surfaced, and there is no runtime PATCH
for any of them; changing them is a deploy-time concern, mirroring how
ADR-010 refuses to expose the Content Safety endpoint URL.
`AgentDefinitionPolicyEndpointContractTests` and
`AgentDefinitionPolicyContractTests` pin those omissions.

## Consequences

* A hostile or drifted definition is rejected at load time in exactly one
  well-audited place, instead of relying on runtime middleware to catch it
  every request.
* Every load-time rejection is visible on `/api/guardrails/log` with a
  `startup-validator` marker, so operators can see the gate is doing its
  job without reading application logs.
* The gate adds one extra pass over the definitions at startup. On the
  disabled path this is regex-only and effectively free; on the enabled
  path it makes at most a few Content Safety calls per unique
  prompt/description, bounded by the roster size.
* `prompts.yaml` remains a **trusted-at-load-time** artifact. This ADR
  does *not* introduce a path for untrusted config to reach the loader —
  the gate is a correctness / drift check, not a sandbox.

## References

* Issue #99 — Safety validation of agent and tenant configuration.
* Issue #98 — Guardrails middleware and request-time layer.
* Issue #100 — Azure AI Content Safety and Prompt Shields integration.
* ADR-008 — `prompts.yaml` trust model.
* ADR-010 — Optional Azure AI Content Safety second layer.
