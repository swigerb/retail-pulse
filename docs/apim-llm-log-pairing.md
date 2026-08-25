# APIM LLM-log Request/Response Pairing Smoke

Documents [`scripts/Verify-ApimLlmLogPairing.ps1`](../scripts/Verify-ApimLlmLogPairing.ps1),
the deterministic smoke test that guards the invariant Publix's non-blocking
observation on PR #52 (issue [#54](https://github.com/swigerb/retail-pulse/issues/54),
item 1) exposed:

> ~30% of small-token direct APIM calls in Publix's sample window produced
> only `SequenceNumber=1` (request) rows in `ApiManagementGatewayLlmLog`
> without a matching `SequenceNumber=0` (response) row. Marker-tagged calls
> did consistently produce populated response records — so the
> acceptance-plan pass criterion is met — but the drop is a regression vs.
> `54aea53` and appears correlated with the `metrics: true` interaction.
> Publix's later 5/5 re-check on `9fdc2ab` showed clean pairing, so behavior
> is inconsistent under short bursts.

The smoke reproduces the burst pattern deterministically and asserts every
marker is paired 1:1 in `ApiManagementGatewayLlmLog` within a bounded settle
window, so any regression is caught before the demo instead of being
observed anecdotally during a live sweep.

## Invariant asserted

For every direct APIM call the smoke fires:

* the request must land in `ApiManagementGatewayLlmLog` with
  `SequenceNumber = 1` **and**
* the corresponding response must land in the same table with
  `SequenceNumber = 0`

within `-SettleSeconds` (default 180s, tunable up to 900s) of the last
call. Both rows must reference the same generated marker (`retail-pulse-marker-<8-hex>`)
in `Content`. The KQL query scopes on that marker via `extract`, so unrelated
traffic in the same workspace never contributes false pairings.

## Assumptions

* An APIM instance is deployed with a chat-completions inference API
  (subscription-required), the API-level `applicationinsights` diagnostic
  set to `metrics = true`, and the `azuremonitor` diagnostic emitting
  `largeLanguageModel.logs = enabled`. These are the exact policy toggles
  that were in effect on `apim-5aldk7aotqods` after `3c39ae4` when Publix
  observed the pairing drop; `scripts/Verify-ApimAiGateway.ps1` verifies
  they are present.
* An operator has APIM subscription-key access to the inference product,
  Reader on the Log Analytics workspace that receives the `azuremonitor`
  diagnostic, and an authenticated `az` CLI session.
* `az` CLI is on `PATH` (the smoke uses `az monitor log-analytics query`
  for the KQL step).

## Parameters

Every parameter has an environment-variable fallback so the smoke can be
wired into a scheduled workflow or a locally-scripted run without shell
tinkering.

| Parameter | Env var | Default | Notes |
| --------- | ------- | ------- | ----- |
| `-Endpoint` | `APIM_INFERENCE_ENDPOINT` | (required) | Inference base URL. `/openai` is appended if not already present. |
| `-Deployment` | `APIM_DEPLOYMENT_NAME` | (required) | AOAI deployment name. |
| `-ApiVersion` | — | `2024-08-01-preview` | Chat-completions API version. |
| `-SubscriptionKey` | `APIM_SUBSCRIPTION_KEY` | (required) | APIM subscription key. Sent as `Ocp-Apim-Subscription-Key`; never logged. |
| `-WorkspaceId` | `APIM_LOG_ANALYTICS_WORKSPACE_ID` | (required) | Log Analytics workspace that receives the `azuremonitor` diagnostic. |
| `-Count` | — | 5 | Number of direct APIM calls to fire (1–100). Matches Publix's original sample. |
| `-SettleSeconds` | — | 180 | Bounded ingest window (30–900s). |
| `-MaxTokens` | — | 8 | Response token cap per call (1–128). Low value reproduces the small-token failure mode. |
| `-SelfTest` | — | (switch) | Offline unit test of the pairing analyzer. Exits without touching APIM or Log Analytics. |

## Invocation

Offline self-test (no signin, no Azure access — wired into CI as a
regression fence):

```powershell
./scripts/Verify-ApimLlmLogPairing.ps1 -SelfTest
```

Live smoke with explicit parameters:

```powershell
./scripts/Verify-ApimLlmLogPairing.ps1 `
    -Endpoint 'https://apim-5aldk7aotqods.azure-api.net/inference' `
    -Deployment 'gpt-4o-mini' `
    -SubscriptionKey $env:APIM_SUBSCRIPTION_KEY `
    -WorkspaceId $env:APIM_LOG_ANALYTICS_WORKSPACE_ID `
    -Count 5 `
    -SettleSeconds 180
```

Live smoke with env-var-driven configuration (typical CI / scheduled use):

```powershell
$env:APIM_INFERENCE_ENDPOINT         = 'https://apim-5aldk7aotqods.azure-api.net/inference'
$env:APIM_DEPLOYMENT_NAME            = 'gpt-4o-mini'
$env:APIM_SUBSCRIPTION_KEY           = '…'
$env:APIM_LOG_ANALYTICS_WORKSPACE_ID = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
./scripts/Verify-ApimLlmLogPairing.ps1
```

## Failure modes

The smoke exits with a distinct exit code per outcome so a scheduled job
can act on each independently:

| Exit | Meaning |
| ---- | ------- |
| `0`  | All markers paired 1:1. Pairing PASS. |
| `1`  | One or more markers missing their request or response row — the exact regression this smoke exists to detect. Details are printed per unpaired marker. Also returned when any direct APIM call itself fails (before ingest analysis can even run). |
| `2`  | Skipped: required parameters missing, or `az` CLI not present. The smoke prints a clear reason and does not attempt any external call. |

The self-test exercises three scenarios equivalent to real failure modes:

1. **Healthy case** — every marker paired. Analyzer must return `Ok = true`.
2. **PR #52 symptom** — one marker missing `SequenceNumber = 0`. Analyzer
   must return `Ok = false`, `MissingResponse = 1`, and name the failing
   marker.
3. **Marker scoping** — a stray request row from a different marker must
   not falsely pair against the current markers.

Every self-test case must pass before the live smoke is trusted; that is
the CI regression fence.

## Live verification status

**Deferred.** Live APIM instance / Log Analytics workspace access is not
available from the environment producing this change. The smoke's offline
`-SelfTest` mode passes locally (see PR body) — but the live half is not
asserted here. When live access is available, run:

```powershell
./scripts/Verify-ApimLlmLogPairing.ps1 -Count 20 -SettleSeconds 300
```

and record the outcome (pairing PASS / FAIL and the marker JSON block) in
a follow-up comment on issue #54.

## Related

* [`Verify-ApimAiGateway.ps1`](../scripts/Verify-ApimAiGateway.ps1) — the
  static-invariant verifier this smoke's conventions mirror. Confirms the
  diagnostic toggles (`metrics = true` on `applicationinsights`,
  `largeLanguageModel.logs = enabled` on `azuremonitor`) that
  `Verify-ApimLlmLogPairing.ps1` depends on.
* PR #52 / issue #51 landed the `metrics = true` toggle on the API-level
  `applicationinsights` diagnostic. Publix's post-merge observation of the
  ingest drop is captured in issue #54 (item 1).
