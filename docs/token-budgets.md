# Per-scope token budgets and the quota-exhaustion demo (#332)

Token budgets are enforced at the APIM AI gateway with the current **`llm-token-limit`** policy (the older
`azure-openai-token-limit` name is superseded and is not used). Each fictional **store**, **channel**,
**application** or **B2B account** gets its own APIM product and subscription, so its tokens-per-minute (TPM)
counter is independent of every other scope instead of one opaque subscription-wide limit.

## How the counter key is derived (trusted context only)

| Item | Source |
| --- | --- |
| Counter key | `context.Subscription.Id` (APIM subscription). The demo counter additionally embeds the demo generation named value. |
| Scope kind | The APIM product id prefix: `rp-budget-store-*`, `rp-budget-channel-*`, `rp-budget-b2b-*`; anything else is `application`. |
| Never used | Request headers, query strings, bodies, JWT claims supplied by a browser. |

Inbound `x-rp-gw-*`, `x-rp-budget-scope` and `x-rp-budget-demo` headers are deleted before the limiter, so a caller
cannot pick, spoof or inflate a scope or force demo mode. APIM subscription keys (`Ocp-Apim-Subscription-Key`,
`api-key`) are deleted before the request reaches the backend. Counter keys and subscription ids are never emitted.

## Infrastructure

`main.bicep` parameters (all idempotent PUTs; subscription keys are service generated and **not** in source or outputs):

| Parameter | Default | Purpose |
| --- | --- | --- |
| `apimTokenBudgetScopes` | three fictional scopes in `main.bicepparam` | `[{ kind, id, displayName }]` -> product `rp-budget-<kind>-<id>` + subscription `rp-budget-sub-<kind>-<id>`. |
| `tokensPerMinute` (module) | `80000` | Production TPM **per scope**. Unchanged by the demo. |
| `apimTokenBudgetDemoEnabled` | `false` | Deploys the demo named values and demo policy branch. |
| `apimTokenBudgetDemoTokensPerMinute` | `50` (max `500`) | Deliberately tiny static TPM used only while armed. |

Read a scope's key at run time (never commit it):
`az apim subscription list-secrets` / the portal. Redeploying resets the demo to disarmed (`rp-budget-demo-armed=false`,
generation `1`), which is itself a reset.

## Evidence (privacy-safe gateway contract)

Emitted through the existing `x-rp-gw-*` contract and surfaced as `GatewayCallEvidence.budget` plus the existing
numeric `tokensConsumed` / `tokensRemaining`:

- `x-rp-gw-budget-scope`: `store|channel|application|b2b`
- `x-rp-gw-budget-mode`: `normal|demo`
- `x-rp-gw-retry-after`: numeric seconds, only on a throttle and only when the limiter published a number
- `x-rp-gw-tokens-remaining`: numeric, when available
- `x-rp-gw-tokens-consumed`: only on admitted calls. A **pre-backend 429 consumed nothing and never reports consumed tokens.**

The API preserves the 429 (`code: gateway_token_budget_exceeded`, `retryAfterSeconds`, `Retry-After`, gateway evidence)
and does not retry it in the SDK. The Control Room renders a *Token budget exhausted* state with the reset walkthrough.

## Operator demo: 429 and reset

The demo uses a **TPM** limit, so exhaustion is **HTTP 429 with `Retry-After`**. A long-period `token-quota`
breach is a different mechanism and returns **403**; this demo does not use `token-quota`.

1. Deploy with `apimTokenBudgetDemoEnabled=true` (disarmed).
2. Arm: `az apim nv update -g <rg> -n <apim> --named-value-id rp-budget-demo-armed --value true`.
3. Send a prompt for a scope; `estimate-prompt-tokens=true` makes the tiny TPM reject deterministically with 429.
4. **Normal reset:** wait the numeric `Retry-After`; the per-minute window then admits calls again.
5. **Immediate demo reset:** APIM has no documented clear-counter API, so bump the generation named value:
   `az apim nv update -g <rg> -n <apim> --named-value-id rp-budget-demo-generation --value <new number>`.
   The demo counter key embeds the generation, so the next call uses a fresh counter. Named-value changes can take
   a few seconds to propagate.
6. Disarm (`rp-budget-demo-armed=false`) to return every scope to the normal production limit.

## Counters are gateway-local

APIM token counters live in the gateway instance that served the call. Multiple regions or gateway resources
(including self-hosted gateways) each keep their **own** counters, so a budget is not a global, cross-region total.
Size per-gateway limits accordingly.

## Live probe

`node scripts/probe-token-budget.cjs --self-test` (offline) and `scripts/probe-token-budget.cjs` against a deployment
(see the script header). Secrets are supplied only via environment variables and are never printed.
