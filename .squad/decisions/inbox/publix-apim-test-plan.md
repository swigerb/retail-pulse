# Publix decision note — APIM AI gateway live-test prep

## Status

Prep only; **not executed yet**.

## Decision

I wrote a concrete live test plan at:

- `docs/testing/apim-ai-gateway-live-test-plan.md`

The plan covers:

1. `azd provision` / `azd up` success
2. APIM Developer SKU + system-assigned identity + cross-RG `Cognitive Services OpenAI User` role assignment
3. Direct APIM inference call with subscription key
4. APIM→AOAI managed-identity backend auth verification
5. Token-per-minute throttling (`429` + `Retry-After`)
6. Token metrics in Application Insights `customMetrics`
7. LLM diagnostics in `ApiManagementGatewayLlmLog`
8. End-to-end app traffic proving the deployed API/frontend path traverses APIM

## Important testing stance

For end-to-end proof, I will treat **APIM telemetry presence + app endpoint config pointing at APIM** as the primary signal. I will **not** use “absence of AOAI logs” as the primary assertion because APIM legitimately forwards requests to AOAI, so downstream service logs may still exist even on a correct APIM path.

## Follow-up owner

I (Publix) will execute this plan live once:

- Kroger lands the final APIM IaC in `infra\`
- Costco lands the app/container-app/azd wiring

At that point I will update any placeholder child-resource names if needed, run the plan against `retailpulse-demo-eus-001`, and report PASS/FAIL with evidence.
