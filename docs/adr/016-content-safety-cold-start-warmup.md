# ADR-016: Content Safety cold-start warm-up and failure classification

## Status

Accepted. Extends [ADR-015](015-tool-result-content-safety-policy.md) and
[ADR-010](010-content-safety-layering.md). Does not change the fail-open or
fail-closed policy set by those ADRs.

## Context

On the deployed app, four Content Safety audit rows clustered inside a 15 second
window immediately after service start:

```
9:43:18 PM  KeywordFastPaths[7] on agent memory-management
9:43:23 PM  SystemPrompt on agent general
9:43:28 PM  DisplayName on agent router
9:43:33 PM  SystemPrompt on agent router
```

Every one recorded `MODEL - SAFETY SERVICE UNAVAILABLE`, status **Allowed
through**: "Content Safety was unreachable when the tool result was scanned, and
the system allowed it because fail-open policy is active." A later call at
11:46:05 PM to the same resource succeeded and correctly blocked an injection
attempt, so the resource is reachable at runtime. This is a cold-start artefact,
not an outage.

Fail-open means these four payloads passed **unscanned**. Four unscanned passes
on every cold start is a security gap, not a logging annoyance.

### Diagnosis

Reading the code establishes the mechanism:

1. **The token fetch shared the scan timeout budget.** `EvaluateAsync` opened one
   `CancellationTokenSource` with `cts.CancelAfter(TimeoutMs)` (default 1500 ms)
   that covered both the managed-identity token acquisition and the scan calls.
   The raw Prompt Shields path fetched its bearer through
   `ContentSafetyTokenProvider` inside that budget, and the SDK moderation path
   fetched its token through the same `DefaultAzureCredential` inside the same
   budget.
2. **The first fetch after start is unprimed.** The first
   `DefaultAzureCredential.GetTokenAsync` has an empty cache and must walk the
   managed-identity chain (IMDS probe plus an AAD round-trip), which routinely
   takes seconds. Folded into a 1500 ms budget that also has to run the scan, it
   expires, surfaces as `OperationCanceledException`, and the call returns
   `ServiceUnavailable`, so fail-open lets the content through.
3. **The startup agent-definition scan is the first caller.** The
   `AgentDefinitionValidator` runs synchronously during host configuration,
   before the host starts, and issues the very first Content Safety calls. Those
   are the four fields in the audit rows (KeywordFastPaths, SystemPrompt twice,
   DisplayName across agents). By 11:46 PM the credential and connection are warm,
   so the same resource answers.
4. **Every failure collapsed into one generic outcome.** All catch blocks returned
   the same `ContentSafetyResult.ServiceUnavailable` singleton, so
   `GuardrailAuditFields.BuildReason` produced identical "unreachable" text for a
   timeout, an auth rejection, and a transport failure. An operator staring at the
   four rows could not tell a cold-start timeout from a 401. Worse, a 401/403
   `RequestFailedException` from the SDK path was not caught at all and would
   propagate rather than route to the fail policy.

What is proven and what is not: the mechanism is established from the code. The
live first-token latency could not be measured, because data-plane RBAC on the
deployed resource was deliberately revoked for the developer identity. The root
cause is therefore strongly narrowed from code, not empirically timed against the
live resource.

## Decision

Four changes, each proportionate to a finding above. None touches the fail-open
policy, the audit visibility, or the counter aggregation owned elsewhere.

1. **Separate the token budget from the scan budget.** `AcquireBearerAsync`
   pre-acquires the bearer under its own `TokenTimeoutMs` budget (default 5000 ms)
   before the scan `CancellationTokenSource` is created. A slow first-token fetch
   can no longer consume the scan timeout. Priming the shared credential here also
   warms the SDK moderation path, because it fetches through the same credential
   singleton and hits the primed cache.

2. **Warm the token before any real scan.** A time-boxed warm-up pre-acquires the
   token so the first real scan is never the one paying the cold login:
   * `ContentSafetyTokenProvider.WarmUpAsync(budget)` fetches and caches a token,
     bounded by the budget, retrying only genuinely transient failures
     (`CredentialUnavailableException`, for example an IMDS endpoint not answering
     yet) with a short backoff. It never retries `AuthenticationFailedException`,
     because a misconfigured identity does not become valid by asking again, and
     it never throws.
   * `ContentSafetyWarmUpService` is a hosted service whose `StartAsync` is
     fire-and-forget, so host startup is never gated on a remote credential call.
     It covers the runtime pipeline.
   * The startup agent-definition scan runs before the host starts and uses a
     separate DI provider, so it is warmed directly in `Program.cs`, best-effort
     and time-boxed, before the validator runs.

3. **Classify the failure.** `ContentSafetyFailureReason`
   (`Timeout`, `Authentication`, `Transport`, `CircuitOpen`) is carried on
   `ContentSafetyResult`, and each catch path sets the matching value. The SDK
   auth gap is closed: 401/403 from `RequestFailedException` and
   `HttpRequestException`, plus `AuthenticationFailedException`, now route to
   `Authentication` rather than escaping.

4. **Name the failure in the audit reason.** `GuardrailAuditFields.UnavailableReason`
   varies the operator-visible text by failure class. Every variant still contains
   the word "unreachable", and the unclassified case keeps the exact original
   sentence, so existing audit consumers see no regression.

Two configuration knobs are added to `ContentSafetyConfig`: `TokenTimeoutMs` and
`WarmUpTimeoutMs`, both defaulting to 5000 ms.

## Consequences

**What an operator sees on cold start now.** When the warm-up succeeds, the first
scan is warm and the four fail-open rows do not appear. If a cold-start fetch
still fails, the row remains visible (fail-open behaviour is unchanged) but its
reason now names the cause, for example "the call timed out before the service
responded" versus "managed-identity authentication was rejected", so a timeout is
no longer indistinguishable from a 401.

**What is unchanged.** The fail-open / fail-closed policy is untouched. Fail-open
rows are not suppressed. The warm-up cannot stall startup: it is fire-and-forget
and time-boxed, and a hanging credential returns `TimedOut` within the budget.
Retry is bounded and applies to transient failures only, never to auth or 4xx.

**Interaction with the fail-open counter work.** A sibling change adds a fail-open
counter on the dashboard. This change alters only the Reason **string**; it does
not change `DetectionType` or `Action` constants, and it does not touch counter
aggregation or the stats API. A counter keyed on the outcome or on the word
"unreachable" continues to match every variant.

**Recommendation left open.** Whether the tool-result stage should fail closed
rather than fail open is a product decision that has not been made, and this
change does not make it. The evidence here is that fail-open silently passed four
unscanned payloads on every cold start. If the owner wants that class of content
guaranteed scanned, the warm-up narrows the window but does not eliminate it, and
only a fail-closed policy for that stage would close it. That decision is left to
the policy owner.

## Follow-up: warming the transport as well as the token (issue #273)

The warm-up above was deployed and measured. The cold-start burst fell from four
fail-open rows to one. The survivor was:

```
SystemPrompt on agent general
"The connection failed before a response."
```

That reason is the `Transport` classification added by decision 3, and it names the
residual precisely: the token was warm, so the first scan no longer paid the AAD
round-trip, but it was still the call opening the connection. DNS, the TCP connect
and the TLS handshake all landed inside the 1500 ms scan budget, leaving too little
of it for the scan itself.

Of the two options this ADR left open, retrying was rejected. The Content Safety
resilience handler documents "no retries" as a deliberate choice, so that the caller
applies its fail-open policy on the first failure rather than multiplying the
per-call latency budget. Adding a retry to the runtime path would contradict a
standing decision to fix a startup-only problem.

So the handshake moves into the warm-up, alongside the token:

* `ContentSafetyWarmUpService` now issues one throwaway request to the endpoint root
  through the shared named `HttpClient` after priming the token. Any response
  establishes the pooled connection; the status is not inspected, because this is a
  handshake and not a health check. Both evaluator paths are registered against that
  same client, so one warm-up primes both.
* Two attempts are allowed. The resilience handler caps every attempt at `TimeoutMs`,
  so extra budget only converts into a better chance of success by re-attempting.
  There is no backoff, because a refused connection is not rate limited.
* Token and transport share one deadline, `WarmUpTimeoutMs`. If the token consumes
  the whole budget the handshake is skipped rather than overrunning the time box,
  because that time box is what keeps warm-up unable to stall startup.
* The startup agent-definition scan calls the same `WarmAsync` the hosted service
  calls. It previously re-implemented the budget and the token call inline, which
  meant the startup path (the one actually producing the fail-open row) could drift
  from the runtime path.

Retrying was not the only thing rejected. Suppressing the fail-open row was too: a
row means content passed unscanned, and hiding it would trade a visible security
signal for a tidy dashboard. The policy remains fail-open and the row remains
visible if the warm-up does not win the race.

## Follow-up: regulated startup diagnostic gap (issue #337)

The bounded regulated activation on 2026-10-23 still refused startup at
`general/SystemPrompt`: 16 definitions, 71 field evaluations, one unavailable
result. The original normal profile was restored; this source-only investigation
does not change the deployed configuration or constitute regulated acceptance.
See [the activation evidence](https://github.com/swigerb/retail-pulse/issues/337#issuecomment-6371493796).

**What is now proven.** The startup mini-provider registered logging configuration
but no logging provider. Its token warm-up, transport warm-up, and evaluator
warnings therefore disappeared, even though the validator's separate console
logger printed the aggregate refusal. The mini-provider now shares that console
logger factory. Fail-closed violations and their audit rows retain the failure
classification, and each unavailable field emits its agent, field, latency and
effective policy before startup can fail. The exception directs operators to
startup logs, not solely to an audit endpoint that a refused host cannot serve.

Evaluator warnings name `Operation` (`Token`, `PromptShield`, `AnalyzeText`),
`FailureReason`, exception/cause **type names**, numeric HTTP status when known,
HTTP request error and socket error when available, elapsed time and effective
scan/token limits. Warm-up logs report the token outcome and transport outcome.
They do not log exception objects/messages, request/response bodies, bearer tokens,
or endpoint values. In particular, an SDK `RequestFailedException` may embed a
response body; logging the exception object is not safe for this diagnostic path.
No new body capture is enabled.

**What this does not prove.** The earlier transport diagnosis above overstated
what the audit classification establishes. `Transport` covers connection errors
**and** selected HTTP failure statuses, so the existing "connection failed before
a response" audit wording alone does not prove DNS/TCP/TLS failure. A successful
unauthenticated root GET proves only that request reached an HTTP response, not
authenticated POST readiness, connection reuse, or future availability.
`general/SystemPrompt` is field 29, not the first field in the shipped default
roster (also verified on the loopback wire).
Neither a cold first POST nor authentication failure has been established as the
production root cause. There is no justified transport repair in this change.

**Offline regression evidence.** `ContentSafetyStartupHostTests` runs the actual
API entrypoint in a child process in Production, with cleared inherited
configuration, temporary storage, a process-local certificate trust file, a
loopback HTTPS Content Safety double and a loopback managed-identity double.
It uses only the shipped synthetic default roster, with normal hydration, and
never calls Azure, models, MCP or live identity providers. Production
authentication remains enabled; audit requests use a locally signed synthetic
test session. The tests prove:

- Healthy regulated startup evaluates all 71 nonempty fields across 16 definitions.
  Hash multisets verify the exact submitted fields and every moderation segment,
  rather than relying only on the aggregate count.
- A successful root GET followed by a controlled connection abort specifically
  at `general/SystemPrompt` still scans the remaining fields and refuses regulated
  startup before listening. The same fault in normal fail-open mode starts with
  exactly the unavailable audit signal; it is not a genuine safety pass.
- Persistent POST connection failures still refuse startup and open the existing
  circuit breaker. All 71 field evaluations are attempted, including those
  short-circuited by the breaker; the wire is not hammered until one passes.
- Real evaluator/SDK error responses distinguish Prompt Shields from moderation,
  preserve status/classification, and omit synthetic body/token canaries and
  exception objects from structured log state.

During this investigation, temporary mutations of the real production source
proved the gates turn red: removing the regulated fail-closed pin failed both
regulated refusal cases (2/4 host cases failed); skipping the definition whose
effective key is `general` reduced the scan to 68 fields and failed all four host
cases. Both mutations were removed before the final regression run.

### Host-test platform prerequisite

The four `ContentSafetyStartupHostTests` cases are explicitly categorized
`LinuxOpenSslIntegration`. They require Linux, the .NET 10 SDK/runtime, OpenSSL
certificate validation honoring `SSL_CERT_FILE`, and available loopback HTTP/TLS
ports. Run the following from the repository root on Linux (including Linux CI
or an appropriately configured WSL/Linux container checkout):

```sh
dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj --configuration Release --filter FullyQualifiedName~ContentSafetyStartupHostTests
```

The existing Ubuntu `Build & Test (.NET)` CI job runs these cases through the
full suite; an unsupported-host skip is not a substitute for that Linux result.
Windows Schannel and macOS native trust do not use this fixture's temporary PEM
as their process-local trust root. On those platforms the four cases are
**visibly skipped at discovery with a prerequisite reason**, not reported as
passed and not allowed to fail later as misleading token timeouts. Portable
evaluator, validator, fail-policy and diagnostic tests continue to run normally.
No system/current-user trust store is modified, TLS verification is not disabled,
and no production hook or transport configuration is introduced.

**Remaining blocker.** Issue #337 remains open. A future, separately authorized
bounded activation must collect these startup phase/status/error diagnostics
at a pinned deployed revision to distinguish token, POST transport, SDK moderation,
timeout and breaker failure. Reproduce the identified mechanism before changing
transport behavior. Preserve fail-closed refusal, all real definition checks,
thresholds, existing token/scan/warm-up budgets and bounded warm-up attempts.
Do not add retries-until-pass or treat a warm-up success as validation. This work
does not authorize deployment, merge, sign-off, issue closure, live prompts or
patient data; synthetic demonstrations must not provide clinical advice.
