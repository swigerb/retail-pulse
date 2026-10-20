# Retailer AI Gateway Control-Plane Delivery Proposal

Status: Proposed — implementation contract for epic #321 (this document is delivered by #322).

Retail Pulse becomes a retailer-specific Azure AI Gateway control-plane demo. This document fixes one architecture path so child issues #323–#342 can be implemented independently without reopening design choices.

## 1. Current state (IaC and repository evidence)

The evidence column cites Bicep, policy XML and source at the PR head. This shows **intended configuration, not deployed state**. Every row is to be confirmed live in #341/#342 using the verification steps below this table.

| Fact | IaC / repository evidence |
| --- | --- |
| One APIM model backend | `infra/modules/apim-openai-api.bicep` defines a single backend `retail-pulse-foundry` (`${aiFoundry.properties.endpoint}openai`) with a 429 circuit breaker (`Retry-After` handling is as documented by Microsoft, see §10). Both `apim-openai-policy.xml` and `apim-openai-models-policy.xml` call `set-backend-service` with that one id. No pool exists. |
| Developer SKU | `infra/modules/apim.bicep`: `sku: { name: 'Developer', capacity: 1 }`. Developer is a non-production tier without an SLA, per Microsoft documentation (§10). |
| Public networking | `infra/modules/apim.bicep`: `publicNetworkAccess: 'Enabled'`, `virtualNetworkType: 'None'`. |
| Content Safety is application-level only | `src/RetailPulse.Api/Middleware/GuardrailsMiddleware.cs` and `src/RetailPulse.Api/Guardrails/` call Content Safety from the API; `infra/modules/content-safety.bicep` provisions the account. The APIM policies contain no `llm-content-safety` policy. |
| Prompt bodies and client IPs are already logged | `infra/modules/apim-openai-api.bicep` `applicationinsights` diagnostic logs frontend and backend request bodies up to 8192 bytes at 100% sampling with `logClientIp: true`; the `azuremonitor` diagnostic enables `largeLanguageModel` logging. This is a privacy item: #337 and the demo default must change it (see §5/§6). |
| Existing APIM policy | `apim-openai-policy.xml` already applies `azure-openai-token-limit` keyed on `context.Subscription.Id` (the same subscription id is the existing `Subscription ID` metric dimension, so re-keying in #332 changes dashboard queries), `azure-openai-emit-token-metric`, and managed-identity auth. It emits only `x-tokens-consumed` and `Retry-After` to callers. |
| No API Center | No `Microsoft.ApiCenter` resource exists in `infra/`. |
| MCP server is internal-only | `infra/modules/container-apps.bicep` sets the MCP server ingress to `external: false` and protects it with an API-key secret. APIM does not front it. APIM is not VNet-injected and `infra/modules/container-apps-env.bicep` has no VNet configuration, so APIM cannot reach an `external: false` app today. |
| Log pairing already documented | `docs/apim-llm-log-pairing.md` describes pairing APIM and LLM logs; the evidence contract must stay consistent with it. |

### Live verification (before #341; record dated output with subscription and tenant ids redacted)

- `az apim show` (SKU, `publicNetworkAccess`, `virtualNetworkType`) and `az apim backend list` (one backend, circuit breaker).
- `az containerapp show` for the MCP app (ingress `external`) and the environment's VNet configuration.
- `az apic list` (expect no API Center) and a Content Safety account check.
- APIM logger/diagnostic resources via `az rest` to confirm body logging, `logClientIp` and `largeLanguageModel` logging.

## 2. Recommended architecture (single path)

APIM stays the single gateway in front of Azure AI Foundry. All additions are APIM policy/Bicep changes plus thin consumers in the API and Web apps. We do not introduce a second gateway product.

### 2.1 Gateway evidence headers (#327, #328)
APIM outbound policy adds a fixed allow-list of response headers: `x-rp-gateway-request-id`, `x-rp-backend-id`, `x-rp-failover` (true/false), `x-rp-safety-verdict`, `x-rp-quota-remaining-tokens`, `x-rp-cache` (hit/miss/bypass), and `x-rp-mode` (demo/regulated). The API forwards only this allow-list to the Web UI; the Gateway Control Room ribbon renders it. Headers never contain prompt or completion text, or user or store identifiers. `x-rp-quota-remaining-tokens` is scoped to the caller's own channel and no channel header is emitted. APIM strips all inbound `x-rp-*` headers before processing so clients cannot spoof evidence (added to #327 acceptance).

### 2.2 Backend pool and failover (#329, #330)
Replace the single backend with two APIM backends (primary and secondary Foundry deployments) grouped in an APIM backend pool (priority-based, with the existing circuit breaker on each member). Policies reference the pool id instead of `retail-pulse-foundry`. The deterministic demo path (#330) is an authorization-gated API endpoint that forces a synthetic 429/5xx on the primary through a policy-recognized header honored only for an authorized demo subscription. APIM strips that header for every other caller, and the endpoint is rate-limited and audited. It never disables a real backend.

### 2.3 APIM Content Safety (#331)
Add the `llm-content-safety` policy to the inbound pipeline, pointing at the existing Content Safety account through a dedicated APIM backend using managed identity (grant the Cognitive Services User role). Enable prompt shields. Configure categories and thresholds in a named value. Blocked requests return 403 with `x-rp-safety-verdict: blocked`. The application-level guardrails remain as defense in depth and are not removed.

### 2.4 Token budgets (#332)
Re-key the existing `azure-openai-token-limit` from subscription id to a per-channel counter key (store/application channel derived server-side from the APIM subscription or a validated JWT claim, never from an unvalidated client-supplied header, to prevent budget evasion), with `remaining-tokens-header-name` and `tokens-consumed-header-name` set. Exhaustion returns 429 with `Retry-After`; a demo endpoint resets nothing — a small budget on a dedicated demo channel exhausts naturally.

### 2.5 API Center and synchronization (#333, #336)
Provision one Azure API Center (Bicep) with a system-assigned identity and an APIM API source integration so APIM APIs and MCP servers are synchronized continuously. Validation (#336) checks that the Retail Pulse MCP server appears with its metadata.

### 2.6 Governed MCP (#334, #335)
**Decision: option (a), external ingress locked down to APIM.** APIM is public (`virtualNetworkType: None`) and the Container Apps environment has no VNet, so an `external: false` app is not addressable from APIM. We keep the demo networking decision (no VNet) and set the MCP app to external ingress restricted so only APIM can use it: the Container App IP restriction allows only APIM's outbound public IP(s) (confirm the Developer SKU address in #334), and the MCP app still requires its API-key secret, which APIM injects from a named value backed by Key Vault or a secret store. VNet integration for both APIM and the environment is the production alternative (§5) and is not part of this epic. The MCP server's ingress is therefore **changed**, not unchanged (§3).

Clients authenticate to APIM (subscription key plus Entra token validation via `validate-jwt`). APIM exposes only an explicit allow-list of read-only operations as MCP tools; no tool is exposed implicitly. #334 records the IP restriction and verifies that direct calls to the MCP FQDN without the key, or from other IPs, fail.

### 2.7 Regulated-retail mode (#337)
A policy-level mode (named value or per-channel setting) that: disables prompt/response body logging (no `log-to-eventhub` of bodies, no body capture or `largeLanguageModel` logging in APIM diagnostics, `logClientIp` off). #337 also changes the demo default so bodies are no longer captured (today 8192 bytes, §1); the demo default keeps metadata and token metrics only, bypasses semantic cache, shortens retention on retained telemetry, and tightens safety thresholds. Mode is visible via `x-rp-mode`. This is a demonstration control for synthetic pharmacy scenarios, not a compliance certification.

### 2.8 Semantic cache (#338, #339)
Use APIM `azure-openai-semantic-cache-lookup`/`-store` with an external Redis-compatible vector cache (Azure Managed Redis with RediSearch; #338) and an embeddings deployment. Eligibility is explicit: cache only requests with no tool calls, no per-user or per-store data, safety verdict allowed, mode demo, and a `cache-eligible` marker; everything else sets `x-rp-cache: bypass`. Cache keys are partitioned per channel/tenant, model, and system-prompt hash via `vary-by`. The cache stores embeddings and completions derived from prompt text in Redis, so it requires a short TTL, encryption at rest and in transit, access restriction, and a documented retention policy. Regulated mode always bypasses.

### 2.9 Retailer packs (#323–#326)
Each pack uses public company name only plus fully synthetic data, with the unofficial/no-affiliation/synthetic-data disclosure, no logos. Packs use the existing content-pack mechanism and are independent of gateway work.

## 3. What changes and what stays unchanged

Changes: the MCP app ingress (external, APIM-only, §2.6), prompt-body and client-IP logging defaults, the token-limit key, APIM Bicep and policy XML (pool, safety, budgets, cache, evidence headers, MCP), new API Center and cache Bicep modules, an API evidence-forwarding path, a Web ribbon, retailer packs, acceptance tests and walkthrough docs.

Unchanged: the application-level guardrails, existing content-pack loading, telemetry and OpenTelemetry pipeline, authentication (Entra) behavior, existing APIM invariants (managed-identity backend auth, token metrics, subscription-key requirement), and all package versions.

## 4. Dependency waves

This table is generated from the `Blocked by` fields of the child issues. If the issues change, this document is the contract and the issues must be updated to match.

| Wave | Issues | Notes |
| --- | --- | --- |
| 0 | #322 | This proposal. |
| 1 | #323, #324, #325, #326 (packs, no blockers); #327, #329, #333, #334, #338 | Blocked by #322 only. |
| 2 | #328 (#327); #331 (#322, #327); #332 (#322, #327); #335 (#333, #334); #339 (#327, #338) | Need wave 1. |
| 3 | #330 (#327, #328, #329); #336 (#333, #335); #337 (#322, #331) | Need waves 1–2. |
| 4 | #340 (#323–#326, #328, #330, #331, #332, #336, #337, #339) | Integrated acceptance and walkthrough. |
| 5 | #341 (#340) | Live deployment by operators after merge. |
| 6 | #342 (#341) | Live proof. |

Shared contract (not a dependency): #337 and #339 both rely on the cache-bypass rule (regulated mode always sets `x-rp-cache: bypass`); #339 must honor it and the two are reviewed together.

Priority mapping: P1 ribbon → #327, #328; P2 failover → #329, #330; P3 safety → #331; P4 budgets → #332; P5 API Center MCP → #333–#336; P6 regulated → #337; P7 semantic cache → #338, #339; retailer packs → #323–#326; proof → #340–#342.

## 5. Demo versus production posture

| Topic | Demo decision | Production decision |
| --- | --- | --- |
| APIM SKU | Keep Developer (cost; no SLA). | Choose the tier per feature: verify support for VNet injection versus outbound-only integration, backend pools, `llm-content-safety`, semantic cache, API Center sync and MCP export against the linked docs. Not every tier with an SLA supports private ingress. |
| Networking | Public network access stays enabled; protected by subscription key plus Entra. | Private ingress on a tier that supports it, VNet integration for APIM and the Container Apps environment (replacing the §2.6 IP-restricted external MCP ingress), and restricted public access. |
| Backend pool | Two deployments in one region. | Multi-region deployments. |
| Quota counters | Per-gateway-instance counters, acceptable on one unit. | Account for per-gateway counters in multi-region/multi-unit deployments, or enforce a central quota. |
| Semantic cache | Small external cache, synthetic data only; prompt text persists in Redis with short TTL. | Sized, access-controlled cache with data-classification review. |
| Logging | Today bodies (8192 bytes) and client IPs are logged (§1); #337 changes the demo default to metadata and token metrics only; regulated mode also disables `logClientIp`. | Metadata only; bodies opt-in only with approved retention. |
| MCP | Read-only allow-listed tools behind APIM; MCP ingress external but APIM-only. | Add per-tool authorization and rate limits. |

Preconditions: confirm the Developer SKU supports each proposed policy (semantic cache needs an external cache; `llm-content-safety`; MCP export) in #329, #331 and #335 before relying on it, and document any limitation.

## 6. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Prompt-body logging leaks sensitive text | Evidence headers and diagnostics carry metadata only; existing body capture (8192 bytes) and `logClientIp` are disabled by #337 for the demo default and regulated mode; tests assert absence of body text in headers and logs. |
| Public APIM network posture | Keep subscription key plus JWT validation; MCP app is externally reachable but IP-restricted to APIM and key-protected; document as demo-only; no secrets in headers; production table above requires private networking. |
| API Center synchronization delay | Treat sync as eventually consistent; #336 polls with a bounded timeout and the walkthrough waits for the sync before showing discovery; no demo step assumes instant propagation. |
| APIM gateway-local quota counters | Token-limit counters are tracked per gateway instance, so limits are approximate across instances; demo uses a single unit and states this; production uses guidance above. |
| Semantic-cache safety | Explicit eligibility/bypass rules, per-tenant `vary-by` partitioning, safety verdict must pass before store, regulated mode always bypasses, similarity threshold set conservatively, TTL short; Redis holds prompt-derived text so encryption and bounded retention apply. |
| Forced-failure path abused | Authorization-gated, demo subscription only, never alters real backend configuration; header stripped for other callers. |
| Header spoofing / budget evasion | APIM strips inbound `x-rp-*` headers; channel derived from subscription or JWT claim only. |
| Company identity misuse | Public names only, synthetic data, no logos, standard disclosure, review against #321 rules. |

## 7. Scope boundaries

In scope: the seven priorities, four packs, API Center sync, governed MCP discovery. Out of scope: replacing APIM, multi-region production rollout, real customer or patient data, compliance certification, changing package versions, and non-listed MCP tools.

## 8. Success criteria

- Every response on the demo path carries the evidence headers; a request with spoofed inbound `x-rp-*` headers returns only APIM-set values.
- Forcing primary failure yields a successful response from the secondary within one request with `x-rp-failover: true`.
- A known prompt-injection sample is blocked at APIM (403, `x-rp-safety-verdict: blocked`) with no model call recorded.
- An exhausted channel budget returns 429 with `Retry-After` present while other channels still succeed.
- Direct requests to the MCP FQDN without the key or from a non-APIM IP fail; allow-listed tools are discoverable through APIM and in API Center after synchronization.
- Regulated mode: a KQL query over APIM/App Insights request-body fields returns 0 rows, and a KQL count of regulated-mode requests with a cache header other than `bypass` equals 0 (100% bypass).
- A repeated eligible prompt returns `x-rp-cache: hit`; ineligible prompts return `bypass`; a different channel, model or system prompt does not hit.
- Existing test suites and APIM invariant tests continue to pass in PR CI with no package version changes.

## 9. Vendor positioning

Azure API Management, API Center, Content Safety, and Azure OpenAI capabilities above are documented Microsoft features. Other gateways, including Kong and Apigee, offer AI-gateway capabilities such as LLM proxying, policy enforcement, and semantic caching in their own products; this proposal chooses APIM because the repository already runs on it, not because any capability is exclusive to Microsoft. Verify feature availability against current vendor documentation before implementation. Links in §10 returned HTTP 200 when checked on 2026-10-20 but page content was not reviewed per claim; re-check tier support at implementation; no feature parity between products is claimed.

## 10. References

- Azure API Management AI gateway: https://learn.microsoft.com/azure/api-management/genai-gateway-capabilities
- Backends and load-balanced pools: https://learn.microsoft.com/azure/api-management/backends
- `llm-content-safety` policy: https://learn.microsoft.com/azure/api-management/llm-content-safety-policy
- `azure-openai-token-limit` policy: https://learn.microsoft.com/azure/api-management/azure-openai-token-limit-policy
- Semantic caching policy: https://learn.microsoft.com/azure/api-management/azure-openai-semantic-cache-lookup-policy
- Expose REST API as MCP server: https://learn.microsoft.com/azure/api-management/export-rest-mcp-server
- Azure API Center and API source sync: https://learn.microsoft.com/azure/api-center/synchronize-api-management-apis
- Kong AI Gateway: https://developer.konghq.com/ai-gateway/
- Apigee documentation (general reference only; no LLM capability claims): https://cloud.google.com/apigee/docs
- APIM feature availability by tier: https://learn.microsoft.com/azure/api-management/api-management-features
