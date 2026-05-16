# Publix — History

## Project Context

- **Project:** Retail Pulse — AI-powered retail analytics on .NET Aspire + React
- **Stack:** .NET 9, Aspire, Azure OpenAI, React/TypeScript/Vite, SignalR, xUnit, Vitest
- **User:** Brian Swiger
- **Joined:** 2026-05-16 (replacing Target)

## Context from predecessor

- 1,915 tests exist (xUnit backend + Vitest frontend)
- Build: `dotnet build RetailPulse.slnx`
- Frontend: `cd src/RetailPulse.Web && npm run build && npx vitest run`
- Lint: `dotnet format RetailPulse.slnx --verify-no-changes`
- Key test files: `tests/RetailPulse.Tests/`
- Demo smoke tests: `tests/RetailPulse.Tests/Agents/DemoQuerySmokeTests.cs`
- Critical demo query: "How is Apex Grill performing in the Southwest this quarter?"

## Learnings

- MaximumIterationsPerRequest=1 was set in Program.cs line 597 and caused ALL tool-using queries to return empty text (the LLM never got a second turn to synthesize after calling tools). This defect was live for multiple sessions before being caught.
- The FallbackReply mechanism works correctly — it fires when response.Text is empty/whitespace. But the ROOT cause was the model never getting to synthesize.
- Smoke tests in DemoQuerySmokeTests.cs test routing but did NOT test actual LLM response generation end-to-end. That gap let this bug through.
- Added `MaxIterationsSynthesisTests.cs` (7 tests) to guard the MaxIterations boundary: uses real `FunctionInvokingChatClient` to prove MaxIterations=1 breaks synthesis and MaxIterations≥2 allows it. These tests will fail immediately if someone regresses MaxIterations back to 1.
- `FunctionInvokingChatClient` in Microsoft.Extensions.AI v10.5.0 takes `(IChatClient, ILoggerFactory?, IServiceProvider?)` — `MaximumIterationsPerRequest` is a settable property, not a constructor options class.
