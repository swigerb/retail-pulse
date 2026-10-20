# Existing-APIM attach-ons

This directory no longer provisions the primary Retail Pulse APIM gateway.

The azd-managed gateway now lives in:

- `infra/modules/apim.bicep`
- `infra/modules/apim-openai-api.bicep`

The remaining file here (`a2a-api.bicep`) is an optional attach-on for wiring an extra API onto an **already-existing** APIM instance in a separate workflow.
