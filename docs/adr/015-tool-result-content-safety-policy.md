# ADR-015: Content Safety policy for tool results

## Status

Accepted (issue #248, resolving the policy question raised by issue #244).

Supersedes nothing. Extends [ADR-010](010-content-safety-layering.md).

## Context

Issue #244 reported that `GetStorePerformance` results were being blocked as
**sexual content at HIGH severity**. The payload is store revenue, targets,
performance percentages, and regions. There is no reading of it as sexual
content, and a blocked tool result is data the user asked for and did not
receive.

The immediate mitigation (PR #247) renamed the generated store format from
*Strip Center* to *Shopping Center*. That was a guess at the trigger, made
without a live Content Safety resource to measure against, and it treated the
symptom rather than the cause.

The incident exposed a policy question. A tool result is data the model is
about to read, so the stage must defend against a hostile document instructing
the model. But the check we actually ran was the opposite one: the full
conversational harm-category scan, with Prompt Shields explicitly **off**.

Retail vocabulary is full of terms that look risky stripped of context: Adult
Beverage, Intimate Apparel, Body Care, Breast Pump Accessories, Strip Center.

Four options were considered on #248:

1. Keep the all-categories scan for every tool result.
2. Add source-aware tool metadata (`trustedStructured` vs `untrustedText`) and
   configure checks per source class.
3. Add field-aware scanning that skips numeric fields and identifiers.
4. Run Prompt Shields on tool results and reserve the harm categories for tools
   that return untrusted natural language.

## Decision

**No tool is trusted into a weaker scan.** Every tool result continues to face
the full all-categories harm scan, whatever its source, and now additionally
faces Prompt Shields.

Concretely:

1. **No source classification.** Options 2 and 4 both require a tool to declare
   its own trust level. A misclassified tool would silently disable a safety
   control, and the misclassification would be invisible until something got
   through. We are not adding a mechanism whose failure mode is a quiet
   downgrade.
2. **Prompt Shields is enabled for the tool-result stage**, and the payload is
   submitted as a **document**, not as a user prompt. The threat a tool result
   carries is data instructing the model, so indirect-injection detection is the
   check that matters. Submitting it as a user prompt would have looked for a
   jailbreak that cannot be there.
3. **Structured output is rendered as prose before it is scanned.** Content
   Safety text moderation is trained on conversational language. Raw JSON is
   not conversational language: a dense run of braces, quotes, commas, and
   camelCase keys carries no sentence structure, and the classifier is being
   asked to score something outside its training distribution.
   `ToolResultTextNormalizer` walks the payload and emits one readable
   `Label: value` line per scalar.

The third point is the substantive fix for #244, and it is deliberately a
**presentation** change rather than a **coverage** change:

* Every scalar is preserved, including numbers and identifiers.
* Every property name is preserved, because an attacker-controlled key is
  content too. Keys naming a container become headings rather than being
  dropped.
* A payload that is not JSON, or that renders to nothing, is scanned verbatim.
  Partial scanning would be a hole in the guardrail, which is worse than the
  noise it would remove.

`ToolResultTextNormalizerTests.Normalize_PreservesEveryScalarAndPropertyName`
is the executable form of that guarantee. It walks the original document and
asserts that every value and every humanised property name appears in the
scanned text. Any future change that starts skipping numeric fields, or exempts
a subtree, fails that test.

## Consequences

**What improves.** The classifier now receives text of the kind it was trained
on. The tool-result stage gains indirect-injection detection it never had, which
is the defence the stage most needed. The policy has no trust classification to
misconfigure.

**What this costs.** One additional Prompt Shields call per tool result, inside
the existing `ContentSafety:TimeoutMs` budget that already covers the whole
evaluation including segmented moderation calls. On a timeout the stage returns
`ServiceUnavailable` and the configured fail policy applies with an audit row,
exactly as before. Operators running large tool payloads with a tight timeout
should review `content-safety-unavailable` rows after enabling this.

**What is not proven.** We cannot measure a false-positive rate without a live
Content Safety resource, and neither could PR #247. What is proven by test is
that coverage is unchanged, that the scanned text is prose rather than JSON,
and that Prompt Shields runs. Whether the observed `GetStorePerformance` block
disappears is a live-deployment observation, not a unit-test result, and #244
should be verified against the deployed Security dashboard.

**The *Shopping Center* rename stays.** Reverting it would be a second
unmeasured guess in the opposite direction. It costs nothing to keep, and
*Shopping Center* is an ordinary retail term.

## Alternatives rejected

**Field-aware scanning (option 3).** Skipping numeric fields would drop
coverage for a class of content that cannot carry harm, which sounds free until
a schema changes and prose arrives in a field the mapping believed was numeric.
The complexity buys a noise reduction that prose rendering already delivers
without giving anything up.

**Reserving harm categories for untrusted text (option 4 in full).** This is a
narrowing of a safety control. It was recorded on #248 as needing owner
approval, and the owner's decision was not to narrow it.
