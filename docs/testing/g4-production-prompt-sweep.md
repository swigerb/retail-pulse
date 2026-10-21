# G4 — live production prompt-acceptance sweep

Gate **G4** of umbrella [#59](https://github.com/swigerb/retail-pulse/issues/59):

> Production sweep against the deployed stack covering every Prompt-ideas +
> Charts entry passes before we call this closed.

`scripts/Invoke-ProductionPromptSweep.ps1` is the runner. It submits all 26
curated prompts from `src/RetailPulse.Web/src/constants/prompts.ts` to the
deployed authenticated `/api/chat` and asserts the G2 contract per prompt.

## Running it

```powershell
az login   # any principal assigned the RetailPulse.User app role
pwsh ./scripts/Invoke-ProductionPromptSweep.ps1 `
    -ApiOrigin https://<api-host> `
    -JsonOut sweep.json
```

Exit code is `0` only when every prompt passes.

For a conservative production acceptance run, add a 30-second delay between
cases:

```powershell
pwsh ./scripts/Invoke-ProductionPromptSweep.ps1 `
    -ApiOrigin https://<api-host> `
    -InterPromptDelaySeconds 30 `
    -JsonOut sweep.json
```

`-InterPromptDelaySeconds` defaults to `0`, must be nonnegative, and sleeps
only between curated cases. It does not delay the first case or alter the
existing per-attempt `Retry-After` handling.

Each request attempt gets a new caller-generated `X-Correlation-ID`. With
`-JsonOut`, every result includes the final `CallerCorrelationId`,
`ReturnedCorrelationId`, sanitized `GatewayRequestIds`, and an `Attempts`
array carrying the same fields for retries. These identifiers cover
successful and failed prompts without persisting auth material, request
payloads beyond the existing curated prompt name, or tool/model response
payloads.

### Authentication

The sweep uses a **delegated** token from the caller's own `az login`
context:

```powershell
az account get-access-token --resource api://<api-client-id>
```

In the sandbox tenant that token carries both `scp=access_as_user` and
`roles=RetailPulse.User`, which is exactly what the API's authorization
policy requires. **No app-only opt-in is needed** — this is a different path
from the optional synthetic monitor (#57), which authenticates as a service
principal and does require `MicrosoftEntra:AllowAppOnlyTokens`.

The token is never printed or logged.

### Rate limiting

`/api/chat` is behind the `strict` fixed-window limiter — 10 permits per
minute, no queue. The runner paces requests ~7s apart and honours
`Retry-After` on a 429.

The normal AI gateway budget is 80,000 tokens per minute. A full unpaced
sweep can still receive a genuine model-token 429 even when the HTTP request
limiter is satisfied, because each curated case can make multiple model
calls. Use `-InterPromptDelaySeconds 30` for the conservative acceptance run.
This pacing is not a quota relaxation and does not erase or reinterpret
previous failed attempts; their correlation and gateway request IDs remain
in the recorded sweep evidence.

## What each prompt must satisfy

1. HTTP 200
2. Non-empty assistant `reply`
3. Routed to a specialist — never the council
4. At least one `tool_call` span
5. No leaked chart JSON in the prose
6. Chart prompts: the expected `ChartSpec` type with at least the manifest's
   `MinMarks` finite marks.
   Prose prompts: **no** chart emitted (the #76 Group A chart-on-prose
   invariant).

### Cache hits are exempt from (4), not from the rest

A cache hit legitimately invokes no tools — that is the point of the cache.
The "at least one tool invoked" contract applies to a fresh execution. A
cached turn is instead held to the stricter [#170](https://github.com/swigerb/retail-pulse/issues/170)
bar: it must still carry the routing and the charts of the answer it
replays, which assertions (3) and (6) enforce for every prompt regardless of
how it was served.

That distinction matters. Before #170 the cache stored only the reply
string, so re-running the sweep inside the 5-minute TTL silently dropped
every chart and made the whole gate unmeasurable.

## Prerequisites cleared to make G4 measurable

| Issue | Problem | Effect on the sweep |
|---|---|---|
| [#168](https://github.com/swigerb/retail-pulse/issues/168) | The Azure OpenAI health probe requested a route APIM never exposed, so `/health` was permanently `Degraded` | Readiness could not distinguish a healthy stack from a broken one |
| [#170](https://github.com/swigerb/retail-pulse/issues/170) | A cache hit dropped `charts[]` and routing | 11 of 26 prompts failed purely from repetition |

## Current result

The 2026-10-21 production run against release
`103bf3242043285a16ec99e7110f2f845f067b73` passed **26/26** with
`-InterPromptDelaySeconds 30`: 26 attempts, zero retries, zero application
cache hits, and gateway request IDs for every case. All chart-bearing cases
passed, including the horizontal brand-ranking chart.

The sanitized per-case correlation evidence is recorded on
[#342](https://github.com/swigerb/retail-pulse/issues/342#issuecomment-6324900122).
That prompt sweep is complete, but #342 remains open until its separate
failover, quota, API Center/MCP, regulated-mode, semantic-cache, and retailer
pack proof requirements are all satisfied.
