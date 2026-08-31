# Squad Team

> Retail Pulse — A generic pro-code agentic demo for retail & consumer goods organizations

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Kroger | Lead | `.squad/agents/kroger/charter.md` | ✅ Active |
| Chick | Frontend Dev | `.squad/agents/chick/charter.md` | ✅ Active |
| Costco | Backend Dev | `.squad/agents/costco/charter.md` | ✅ Active |
| Publix | Tester | `.squad/agents/publix/charter.md` | ✅ Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | `.squad/agents/ralph/charter.md` | 🔄 Monitor |

## Merge Authority

Every agent, and the orchestrator, pushes with the same `swigerb` token. GitHub
therefore sees one human, and a `required_approving_review_count` rule would
deadlock every PR instead of protecting anything. Authority is enforced by a
deliberate, logged act instead.

**No agent may merge its own pull request.** On 2026-08-31 five PRs merged
themselves inside 31 minutes with no review, and attribution was unrecoverable
because `mergedBy` read `swigerb` for all of them.

`dev` and `main` both require the `Squad lead sign-off` status check, published by
`.github/workflows/squad-lead-gate.yml`. It stays red until a reviewer with push
access posts a comment on the PR:

```
Squad-Lead-Approved: <head-sha>
Reviewer: <agent or person>
Evidence: <test run or verification link>
```

The SHA pin is what makes this real. Push another commit and the check flips back
to red on its own, so nothing can be amended in behind an approval.

| Rule | Who |
|------|-----|
| Decides whether a PR merges | Brian, or Kroger acting as Lead |
| Posts the sign-off comment | Only on Brian's explicit instruction |
| Runs `gh pr merge` | Only after the sign-off check is green |
| Pushes directly to `dev` or `main` | Nobody. Neither branch has a bypass actor. |

The orchestrator never posts a sign-off comment on its own initiative and never
merges unprompted. Reviewing a PR and approving it are separate acts.

### The one automatic exemption

The gate passes itself when a PR's commits are **already on `main`**. Such a PR
introduces no new code, because everything in it cleared this same gate on the way
into the release branch. This is a statement about content rather than about who
opened the PR or what the branch is called, so a PR that actually changes something
can never borrow it. PRs into `main` are excluded and always need a real sign-off.

That exemption is what lets `squad-sync-dev.yml` keep `dev` level with `main` without
a bypass. GitHub refuses the Actions integration as a bypass actor outside an
organization, and the only bypasses a personal repo accepts are the admin and
maintain roles, which is exactly the access every agent already holds through the
shared token. Granting one would have protected nothing.

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Owner:** Brian Swiger
- **Stack:** .NET 10, C#, Aspire (host + OTel, non-containerized), React/Vite/TypeScript, Azure API Management, AI Gateway pattern
- **Description:** Retail Pulse — a generic pro-code agentic demo for retail & consumer goods organizations (grocers, QSRs, big box retail). Built on Patron Pulse but updated to be generic with tenant configuration, extra organization examples, and corrected diagrams.
- **Created:** 2026-04-30
