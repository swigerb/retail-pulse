# Existing-APIM attach-ons

This directory no longer provisions the primary Retail Pulse APIM gateway.

The azd-managed gateway now lives in:

- `infra/modules/apim.bicep`
- `infra/modules/apim-openai-api.bicep`

The remaining files here (`mcp-api.bicep`, `a2a-api.bicep`) are optional attach-ons for wiring extra APIs onto an **already-existing** APIM instance in a separate workflow.
