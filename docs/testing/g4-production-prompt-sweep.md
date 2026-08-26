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
`Retry-After` on a 429, so a full sweep never trips the limiter.

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

Stable at **22/26** across three consecutive runs. **G4 has not passed.**
The four outstanding prompts are tracked in
[#172](https://github.com/swigerb/retail-pulse/issues/172).

G3 — `Show a horizontal bar chart ranking all brands by depletion growth
rate` — renders a `horizontalBar` with ≥6 finite marks on every run.
