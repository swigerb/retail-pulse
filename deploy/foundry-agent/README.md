# Foundry Agent Deployment Tool

> Standalone CLI for deploying the Distribution Analysis specialist agent to Azure AI Foundry.

## Why this lives outside the solution

This project is **intentionally excluded** from `RetailPulse.slnx`. It is a one-shot deployment utility — not part of the runtime — and is built and run on demand:

```bash
dotnet run --project deploy/foundry-agent
```

Keeping it out of the main solution avoids:

- Restoring its (heavy) Azure SDK dependencies on every Aspire build.
- Coupling agent provisioning to application lifecycle.
- Test discovery picking up its `Program.cs` entry point.

## When to run it

Only when you opt into the Foundry-hosted multi-agent experience by setting `FoundryAgent:Enabled=true` in the API configuration. By default, Retail Pulse uses the local in-process `LocalShipmentAnalyzer` and this tool is not needed.

## Configuration

The deployment tool reads the same `FoundryAgent:ProjectEndpoint` setting as the API. Sign in with `az login` first — it uses `DefaultAzureCredential`.

## Package management

The project shares the repo's `Directory.Packages.props` for centrally-managed package versions, so its dependencies stay aligned with the rest of the platform.
