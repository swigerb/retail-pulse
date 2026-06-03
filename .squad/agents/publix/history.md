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
- API versioning tests (`Endpoints/ApiVersioningTests.cs`, 13 tests, 2026-06-03): mirror the exact `ApiVersioningOptions` from `Program.cs` inside a `TestServer` and attach versioned endpoints via `endpoints.NewApiVersionSet().HasApiVersion(...).Build()` + `WithApiVersionSet(versionSet).MapToApiVersion(...)`. This pattern avoids the project's Azure-credential startup cost while still exercising the real `Asp.Versioning.Http` middleware. Suite passes on both 8.1.0 and 10.0.0 — used to gate Costco's upgrade.
- Asp.Versioning gotcha: with `UrlSegmentApiVersionReader`, an unsupported version (`/api/v99/...`) returns **404**, not 400, because the version is part of the route template and no route matches. The 400-from-middleware shape only applies to header/query readers. The `AssumeDefaultVersionWhenUnspecified=true` option also cannot rescue a missing URL segment — a request to `/api/health` (no `v{n}`) still 404s. Tests must be written with that asymmetry in mind, or they'll false-fail.
- Asp.Versioning route token `{version:apiVersion}` is consumed by middleware, not bound by the minimal-API handler, so ASP0018 ("unused route parameter") fires unless suppressed with `#pragma warning disable ASP0018` at the top of the test file.
- Production endpoints in RetailPulse.Api are **not** versioned at the route level (all are `/api/...` with no `v{n}` segment) even though `AddApiVersioning` is configured. The versioning service is wired but inert at the route layer. When future versioned routes are added, the contract tests in `ApiVersioningTests.cs` give us the template.
