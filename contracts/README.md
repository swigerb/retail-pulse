# Client/API contract fixtures

This folder holds the shared contract between the API response shapes and the
TypeScript types the SPA consumes. Both test suites assert against the same
committed JSON files, so a field renamed, removed, or retyped on either side
fails a test that names the endpoint and the field.

## Why this exists

Over one development cycle, five separate client/API contract mismatches reached
the deployed app and were caught by a human clicking through the live UI: a page
rendered empty, a value showed as zero, a feature looked broken while the backend
was fine. In every case the client read a field the API did not send, or sent
under a different name or shape. Both sides had green tests: the C# tests asserted
what the API produced, the TypeScript tests asserted what the client consumed, and
nothing asserted that the two agreed. These fixtures close that gap.

## How it works (round-trip fixtures)

1. The backend test
   `tests/RetailPulse.Tests/ContractFixtures/ApiClientContractFixtureTests.cs`
   builds the real response DTO for each covered endpoint and serialises it with
   the API's own JSON options (`JsonSerializerDefaults.Web`: camelCase, integer
   enums, ISO-8601 dates). It compares the result structurally against the
   committed fixture in this folder and fails with a per-path diff on any drift.

2. The frontend test
   `src/RetailPulse.Web/src/__tests__/apiClientContract.contract.test.ts` loads
   the same fixtures, feeds each one through the real client service or mapper
   (for example `fetchGuardrailsStats`, `fetchGuardrailsLog`, `fetchActiveCards`,
   `fetchScorecard`), and asserts every field the SPA reads is present and
   correctly typed. If the client starts reading a wire field the fixture (the
   real API shape) does not contain, this test fails.

Together this means:

- API renames, removes, or retypes a field -> the C# snapshot test fails.
- Client reads a field the API does not send -> the TypeScript test fails.
- An additive API change (for example a new counter) shows up as a reviewable
  diff in the regenerated fixture, which is easy to accept on purpose.

No server and no Azure resources are required: the fixtures are static files, so
this runs cheaply on every PR.

## Updating a fixture (deliberate, reviewable)

Fixtures are never regenerated silently. When an API change is intentional,
regenerate and commit the diff:

```powershell
# PowerShell
$env:UPDATE_CONTRACT_FIXTURES = '1'
dotnet test tests\RetailPulse.Tests\RetailPulse.Tests.csproj --configuration Release `
  --filter "FullyQualifiedName~ApiClientContractFixtureTests"
Remove-Item Env:\UPDATE_CONTRACT_FIXTURES
```

```bash
# bash
UPDATE_CONTRACT_FIXTURES=1 dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj \
  --configuration Release --filter "FullyQualifiedName~ApiClientContractFixtureTests"
```

Review the resulting fixture diff, then update the client mapper and its
assertions to match before committing. The regeneration is an explicit act, so a
broken shape cannot pass by quietly rewriting the snapshot.

## Coverage

Covered (the API serialises a DTO directly, so both sides are unambiguous):

- `GET /api/guardrails/stats` -> `guardrails-stats.json`
- `GET /api/guardrails/log` -> `guardrails-log.json`
- `GET /api/cards` -> `cards.json`
- `POST /api/scorecard` -> `portfolio-scorecard.json`

Deferred (documented in the PR): Health Council, Campaign Planner (Promo), and
Financials (Margin). Their wire shapes are hand-built in the endpoint or owned by
the McpServer process, so there is no directly-serialisable Api/Contracts DTO to
round-trip without a production refactor or an in-memory host.
