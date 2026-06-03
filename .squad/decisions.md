# Squad Decisions

## Active Decisions

### 2026-05-20T08:43:48Z: User directive
**By:** Brian Swiger (via Copilot)
**What:** The project owner's name is Brian Swiger (not "Brady"). Always address them as Brian.
**Why:** User request — captured for team memory

# Deferred major-version package bumps (2026-06-03)

**Author:** Costco (Backend Dev)
**Date:** 2026-06-03T11:13:12.666-04:00

During the NuGet upgrade sweep, two packages had stable latest versions available but were deliberately **not upgraded** in this pass because they require focused testing beyond a "build + unit tests" gate:

## 1. Asp.Versioning.Http: 8.1.0 → 10.0.0

- Two major-version skip (8 → 10).
- Known breaking changes in URL segment/header conventions and API explorer integration.
- Risk: every versioned endpoint contract could shift; FE callers and OpenAPI consumers need re-validation.
- **Action item:** A separate task should upgrade this with explicit contract regression tests on `/api/v{n}/...` routes, and Chick should be looped in for FE client regeneration.

## 2. coverlet.collector: 6.0.4 → 10.0.1

- Four-major-version skip.
- Collector configuration and output formats have changed; CI coverage reporting (if wired) may need updates.
- **Action item:** Bump only when someone owns validating the coverage pipeline end-to-end.

## What WAS upgraded successfully
See costco/history.md entry for full list. Notable majors that did go through cleanly: `Microsoft.Data.Sqlite 9→10`, `YamlDotNet 17→18`, `Microsoft.NET.Test.Sdk 17→18`. Build is clean (0 warnings) and 1,926 tests pass.

## Compatibility footnotes for the team
- **Microsoft.IdentityModel.Protocols.OpenIdConnect** pinned at 8.18.0 (latest is 8.19.1) because its peer `System.IdentityModel.Tokens.Jwt` only publishes through 8.18.0 — bumping OIDC further causes NU1102.
- **Microsoft.Bcl.Memory** bumped to 10.0.7 to satisfy `Microsoft.Agents.Connector 1.5.184` transitive requirement; future Agents stack bumps may continue pulling Bcl.Memory forward.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
