# Retailer AI Gateway Control-Plane Delivery Proposal

Status: Proposed — implementation contract for epic #321 (this document is delivered by #322).

Retail Pulse becomes a retailer-specific Azure AI Gateway control-plane demo. This document fixes one architecture path so child issues #323–#342 can be implemented independently without reopening design choices.

## 1. Current state (evidence from the repository)

| Fact | Evidence |
| --- | --- |
| One APIM model backend | `infra/modules/apim-openai-api.bicep` defines a single backend `retail-pulse-foundry` (`${aiFoundry.properties.endpoint}openai`) with a 429 circuit breaker. Both `apim-openai-policy.xml` and `apim-openai-models-policy.xml` call `set-backend-service` with that one id. No pool exists. |
| Developer SKU | `infra/modules/apim.bicep`: `sku: { name: 'Developer', capacity: 1 }`. Developer is a non-production tier without an SLA. |
| Public networking | `infra/modules/apim.bicep`: `publicNetworkAccess: 'Enabled'`, `virtualNetworkType: 'None'`. |
| Content Safety is application-level only | `src/RetailPulse.Api/Middleware/GuardrailsMiddleware.cs` and `src/RetailPulse.Api/Guardrails/` call Content Safety from the API; `infra/modules/content-safety.bicep` provisions the account. The APIM policies contain no `llm-content-safety` policy. |
| Existing APIM policy | `apim-openai-policy.xml` already applies `azure-openai-token-limit` keyed on `context.Subscription.Id`, `azure-openai-emit-token-metric`, and managed-identity auth. It emits only `x-tokens-consumed` and `Retry-After` to callers. |
| No API Center | No `Microsoft.ApiCenter` resource exists in `infra/`. |
| MCP server is internal-only | `infra/modules/container-apps.bicep` sets the MCP server ingress to `external: false` and protects it with an API-key secret. APIM does not front it. |
| Log pairing already documented | `docs/apim-llm-log-pairing.md` describes pairing APIM and LLM logs; the evidence contract must stay consistent with it. |

## 2. Recommended architecture (single path)

APIM stays the single gateway in front of Azure AI Foundry. All additions are APIM policy/Bicep changes plus thin consumers in the API and Web apps. We do not introduce a second gateway product.

### 2.1 Gateway evidence headers (#327, #328)
APIM outbound policy adds a fixed allow-list of response headers: `x-rp-gateway-request-id`, `x-rp-backend-id`, `x-rp-failover` (true/false), `x-rp-safety-verdict`, `x-rp-quota-remaining-tokens`, `x-rp-cache` (hit/miss/bypass), and `x-rp-mode` (demo/regulated). The API forwards only this allow-list to the Web UI; the Gateway Control Room ribbon renders it. Headers never contain prompt or completion text, user identifiers, or store identifiers beyond the budget channel name.

### 2.2 Backend pool and failover (#329, #330)
Replace the single backend with two APIM backends (primary and secondary Foundry deployments) grouped in an APIM backend pool (priority-based, with the existing circuit breaker on each member). Policies reference the pool id instead of `retail-pulse-foundry`. The deterministic demo path (#330) is an authorization-gated API endpoint that forces a synthetic 429/5xx on the primary through a policy-recognized header honored only for an authorized demo subscription; it never disables a real backend.

### 2.3 APIM Content Safety (#331)
Add the `llm-content-safety` policy to the inbound pipeline, pointing at the existing Content Safety account through a dedicated APIM backend using managed identity (grant the Cognitive Services User role). Enable prompt shields. Configure categories and thresholds in a named value. Blocked requests return 403 with `x-rp-safety-verdict: blocked`. The application-level guardrails remain as defense in depth and are not removed.

### 2.4 Token budgets (#332)
Re-key the existing `azure-openai-token-limit` from subscription id to a per-channel counter key (store/application channel resolved from the APIM subscription or a validated header), with `remaining-tokens-header-name` and `tokens-consumed-header-name` set. Exhaustion returns 429 with `Retry-After`; a demo endpoint resets nothing — a small budget on a dedicated demo channel exhausts naturally.

### 2.5 API Center and synchronization (#333, #336)
Provision one Azure API Center (Bicep) with a system-assigned identity and an APIM API source integration so APIM APIs and MCP servers are synchronized continuously. Validation (#336) checks that the Retail Pulse MCP server appears with its metadata.

### 2.6 Governed MCP (#334, #335)
APIM exposes selected operations of the existing MCP server as MCP tools, so the MCP container stays `external: false` and APIM reaches it through the Container Apps environment boundary. #334 fixes the authentication boundary: clients authenticate to APIM (subscription key plus Entra token validation via `validate-jwt`); APIM authenticates to the MCP container with the existing API-key secret held in a named value backed by Key Vault or secret store. Only an explicit allow-list of read-only tools is exposed; no tool is exposed implicitly.

### 2.7 Regulated-retail mode (#337)
A policy-level mode (named value or per-channel setting) that: disables prompt/response body logging (no `log-to-eventhub` of bodies, no body capture in APIM diagnostics), bypasses semantic cache, shortens retention on retained telemetry, and tightens safety thresholds. Mode is visible via `x-rp-mode`. This is a demonstration control for synthetic pharmacy scenarios, not a compliance certification.

### 2.8 Semantic cache (#338, #339)
Use APIM `azure-openai-semantic-cache-lookup`/`-store` with an external Redis-compatible vector cache (Azure Managed Redis with RediSearch; #338) and an embeddings deployment. Eligibility is explicit: cache only requests with no tool calls, no per-user or per-store data, safety verdict allowed, mode demo, and a `cache-eligible` marker; everything else sets `x-rp-cache: bypass`. Cache keys are partitioned per channel/tenant via `vary-by`. Regulated mode always bypasses.

### 2.9 Retailer packs (#323–#326)
Each pack uses public company name only plus fully synthetic data, with the unofficial/no-affiliation/synthetic-data disclosure, no logos. Packs use the existing content-pack mechanism and are independent of gateway work.

## 3. What changes and what stays unchanged

Changes: APIM Bicep and policy XML (pool, safety, budgets, cache, evidence headers, MCP), new API Center and cache Bicep modules, an API evidence-forwarding path, a Web ribbon, retailer packs, acceptance tests and walkthrough docs.

Unchanged: the application-level guardrails, existing content-pack loading, telemetry and OpenTelemetry pipeline, authentication (Entra) behavior, existing APIM invariants (managed-identity backend auth, token metrics, subscription-key requirement), the MCP server's non-public ingress, and all package versions.

## 4. Dependency waves

| Wave | Issues | Notes |
| --- | --- | --- |
| 0 | #322 | This proposal. |
| 1 (parallel) | #323, #324, #325, #326; #327; #329; #333; #334; #338 | Packs, evidence contract, backend pool, API Center, MCP design, cache infra. |
| 2 | #328 (needs #327); #330 (needs #329, #327); #331 (needs #327); #332 (needs #327); #335 (needs #334); #336 (needs #333, #335) | Features on the evidence contract. |
| 3 | #337 (needs #331, #332, #339 bypass contract); #339 (needs #338, #327) | Regulated mode and semantic cache policy; #337 and #339 share the bypass rule and are reviewed together. |
| 4 | #340 | Integrated acceptance and walkthrough. |
| 5 | #341, #342 | Live deployment and proof, performed by operators after merge. |

Priority mapping: P1 ribbon → #327, #328; P2 failover → #329, #330; P3 safety → #331; P4 budgets → #332; P5 API Center MCP → #333–#336; P6 regulated → #337; P7 semantic cache → #338, #339; retailer packs → #323–#326; proof → #340–#342.

## 5. Demo versus production posture

| Topic | Demo decision | Production decision |
| --- | --- | --- |
| APIM SKU | Keep Developer (cost; no SLA). | Use Premium v2 or Standard v2/Premium per Microsoft guidance for SLA and VNet needs. |
| Networking | Public network access stays enabled; protected by subscription key plus Entra. | Private endpoints/VNet integration and restricted public access. |
| Backend pool | Two deployments in one region. | Multi-region deployments. |
| Quota counters | Per-gateway-instance counters, acceptable on one unit. | Account for per-gateway counters in multi-region/multi-unit deployments, or enforce a central quota. |
| Semantic cache | Small external cache, synthetic data only. | Sized, access-controlled cache with data-classification review. |
| Logging | Metadata only. | Same; bodies opt-in only with approved retention. |
| MCP | Read-only allow-listed tools. | Add per-tool authorization and rate limits. |

## 6. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Prompt-body logging leaks sensitive text | Evidence headers and diagnostics carry metadata only; body capture disabled by default and in regulated mode; tests assert absence of body text in headers and logs. |
| Public APIM network posture | Keep subscription key plus JWT validation; document as demo-only; no secrets in headers; production table above requires private networking. |
| API Center synchronization delay | Treat sync as eventually consistent; #336 polls with a bounded timeout and the walkthrough waits for the sync before showing discovery; no demo step assumes instant propagation. |
| APIM gateway-local quota counters | Token-limit counters are tracked per gateway instance, so limits are approximate across instances; demo uses a single unit and states this; production uses guidance above. |
| Semantic-cache safety | Explicit eligibility/bypass rules, per-tenant `vary-by` partitioning, safety verdict must pass before store, regulated mode always bypasses, similarity threshold set conservatively, TTL short. |
| Forced-failure path abused | Authorization-gated, demo subscription only, never alters real backend configuration. |
| Company identity misuse | Public names only, synthetic data, no logos, standard disclosure, review against #321 rules. |

## 7. Scope boundaries

In scope: the seven priorities, four packs, API Center sync, governed MCP discovery. Out of scope: replacing APIM, multi-region production rollout, real customer or patient data, compliance certification, changing package versions, and non-listed MCP tools.

## 8. Success criteria

- Every response on the demo path carries the evidence headers; ribbon shows backend, failover, safety, quota, and cache state.
- Forcing primary failure yields a successful response from the secondary with `x-rp-failover: true` in a single request.
- A known prompt-injection sample is blocked at APIM (403) before reaching the model.
- An exhausted channel budget returns 429 with `Retry-After` while other channels still succeed.
- The Retail Pulse MCP server is visible in API Center after synchronization and its allow-listed tools are discoverable.
- Regulated mode produces zero prompt/response bodies in headers or logs and 100% cache bypass.
- A repeated eligible prompt returns `x-rp-cache: hit`; ineligible prompts return `bypass`.
- Existing test suites and APIM invariant tests continue to pass in PR CI with no package version changes.

## 9. Vendor positioning

Azure API Management, API Center, Content Safety, and Azure OpenAI capabilities above are documented Microsoft features. Other gateways, including Kong and Apigee, offer AI-gateway capabilities such as LLM proxying, policy enforcement, and semantic caching in their own products; this proposal chooses APIM because the repository already runs on it, not because any capability is exclusive to Microsoft. Verify feature availability against current vendor documentation before implementation.

## 10. References

- Azure API Management AI gateway: https://learn.microsoft.com/azure/api-management/genai-gateway-capabilities
- Backends and load-balanced pools: https://learn.microsoft.com/azure/api-management/backends
- `llm-content-safety` policy: https://learn.microsoft.com/azure/api-management/llm-content-safety-policy
- `azure-openai-token-limit` policy: https://learn.microsoft.com/azure/api-management/azure-openai-token-limit-policy
- Semantic caching policy: https://learn.microsoft.com/azure/api-management/azure-openai-semantic-cache-lookup-policy
- Expose REST API as MCP server: https://learn.microsoft.com/azure/api-management/export-rest-mcp-server
- Azure API Center and API source sync: https://learn.microsoft.com/azure/api-center/synchronize-api-management-apis
- Kong AI Gateway: https://developer.konghq.com/ai-gateway/
- Apigee API management: https://cloud.google.com/apigee/docs
