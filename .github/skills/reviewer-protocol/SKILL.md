---
name: "reviewer-protocol"
description: "Reviewer rejection workflow and strict lockout semantics"
domain: "orchestration"
confidence: "high"
source: "extracted"
---

## Context

When a team member has a **Reviewer** role (e.g., Tester, Code Reviewer, Lead), they may approve or reject work from other agents. On rejection, the coordinator enforces strict lockout rules to ensure the original author does NOT self-revise. This prevents defensive feedback loops and ensures independent review.

Reviewers also gate merge. CI green means the code compiled and the tests that ran passed — it does not mean the change is correct and it does not mean the suite is stable. The merge gate rules below are non-negotiable.

## Patterns

### Merge Gate — Verdicts, Not CI Alone

These rules bind every PR in this repository, including docs-only PRs.

1. **Never merge on CI status alone.** Before merging, confirm an explicit **APPROVE** verdict for the **current head SHA**. Green checks are necessary but not sufficient.
2. **REJECT blocks merge**, regardless of check status. Do not merge a PR whose most recent verdict for the current head is REJECT, even if every check is green. Merge only after the reviewer posts a fresh APPROVE against the current head.
3. **Verdicts must be posted as PR comments.** Both APPROVE and REJECT are recorded as comments on the pull request itself. A verdict that exists only in an agent session transcript is not a verdict for gate purposes.
4. **Read the diff before merging a defect fix.** For any PR that claims to fix a bug or regression, inspect the diff and confirm the change actually does what the PR description says.
5. **Re-measure stability claims on the merge target.** Stability sweeps ("N consecutive passes", flake reproduction attempts) must be run against the PR's mergeable head with the target merged in — not against the working branch under different load. Report **raw per-run output**. If any run fails during a stability sequence — target or not — **name the failing test**. Do not report a clean sweep that omits observed failures.

#### Why verdicts are comments — the single-identity constraint

Author and reviewer in this repository share the same GitHub identity, **`swigerb`**. GitHub blocks formal self-approval, so a reviewer running under `swigerb` **cannot** submit a Files-changed → Approve review on a PR authored by `swigerb`. Verdicts are therefore recorded as **PR comments**. A merge gate that only reads formal GitHub review state (`APPROVED` / `CHANGES_REQUESTED`) will see zero verdicts on every PR and will let REJECTs through. The gate — human or tooling — must read the **verdict comment for the current head SHA**, not the formal review state and not the CI status.

#### Verdict staleness

An APPROVE applies to the exact head SHA it was posted against. Any new commit invalidates it — the PR must receive a fresh APPROVE against the new head before it may merge. A REJECT similarly applies until the reviewer posts a new verdict against a newer head.

#### Anti-patterns for the merge gate

- ❌ Merging because checks are green without locating an APPROVE comment for the current head
- ❌ Merging over a REJECT comment because CI is green or because the reviewer's concerns "seem minor"
- ❌ Leaving a verdict in a session transcript instead of posting it to the PR
- ❌ Merging a defect fix without reading the diff to confirm the change matches the PR description
- ❌ Reporting a clean stability sweep run on the working branch instead of the merge target
- ❌ Reporting a clean stability sweep that silently drops runs where a test failed — every observed failure must be named
- ❌ Inferring a verdict from formal GitHub review state alone (self-approval is blocked by the shared `swigerb` identity, so formal state is always empty)

### Reviewer Rejection Protocol

When a team member has a **Reviewer** role:

- Reviewers may **approve** or **reject** work from other agents.
- On **rejection**, the Reviewer may choose ONE of:
  1. **Reassign:** Require a *different* agent to do the revision (not the original author).
  2. **Escalate:** Require a *new* agent be spawned with specific expertise.
- The Coordinator MUST enforce this. If the Reviewer says "someone else should fix this," the original agent does NOT get to self-revise.
- If the Reviewer approves, work proceeds normally.

### Strict Lockout Semantics

When an artifact is **rejected** by a Reviewer:

1. **The original author is locked out.** They may NOT produce the next version of that artifact. No exceptions.
2. **A different agent MUST own the revision.** The Coordinator selects the revision author based on the Reviewer's recommendation (reassign or escalate).
3. **The Coordinator enforces this mechanically.** Before spawning a revision agent, the Coordinator MUST verify that the selected agent is NOT the original author. If the Reviewer names the original author as the fix agent, the Coordinator MUST refuse and ask the Reviewer to name a different agent.
4. **The locked-out author may NOT contribute to the revision** in any form — not as a co-author, advisor, or pair. The revision must be independently produced.
5. **Lockout scope:** The lockout applies to the specific artifact that was rejected. The original author may still work on other unrelated artifacts.
6. **Lockout duration:** The lockout persists for that revision cycle. If the revision is also rejected, the same rule applies again — the revision author is now also locked out, and a third agent must revise.
7. **Deadlock handling:** If all eligible agents have been locked out of an artifact, the Coordinator MUST escalate to the user rather than re-admitting a locked-out author.

## Examples

**Example 1: Reassign after rejection**
1. Fenster writes authentication module
2. Hockney (Tester) reviews → rejects: "Error handling is missing. Verbal should fix this."
3. Coordinator: Fenster is now locked out of this artifact
4. Coordinator spawns Verbal to revise the authentication module
5. Verbal produces v2
6. Hockney reviews v2 → approves
7. Lockout clears for next artifact

**Example 2: Escalate for expertise**
1. Edie writes TypeScript config
2. Keaton (Lead) reviews → rejects: "Need someone with deeper TS knowledge. Escalate."
3. Coordinator: Edie is now locked out
4. Coordinator spawns new agent (or existing TS expert) to revise
5. New agent produces v2
6. Keaton reviews v2

**Example 3: Deadlock handling**
1. Fenster writes module → rejected
2. Verbal revises → rejected
3. Hockney revises → rejected
4. All 3 eligible agents are now locked out
5. Coordinator: "All eligible agents have been locked out. Escalating to user: [artifact details]"

**Example 4: Reviewer accidentally names original author**
1. Fenster writes module → rejected
2. Hockney says: "Fenster should fix the error handling"
3. Coordinator: "Fenster is locked out as the original author. Please name a different agent."
4. Hockney: "Verbal, then"
5. Coordinator spawns Verbal

## Anti-Patterns

- ❌ Allowing the original author to self-revise after rejection
- ❌ Treating the locked-out author as an "advisor" or "co-author" on the revision
- ❌ Re-admitting a locked-out author when deadlock occurs (must escalate to user)
- ❌ Applying lockout across unrelated artifacts (scope is per-artifact)
- ❌ Accepting the Reviewer's assignment when they name the original author (must refuse and ask for a different agent)
- ❌ Clearing lockout before the revision is approved (lockout persists through revision cycle)
- ❌ Skipping verification that the revision agent is not the original author
