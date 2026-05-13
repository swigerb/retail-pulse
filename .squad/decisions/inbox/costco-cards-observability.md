# Decision: Card State & Observability Architecture (Sprint 3.3 + 3.4)

**Author:** Costco (Backend Dev)
**Date:** 2026-05-13
**Status:** Implemented

## Context

Sprint 3.3 (Card State & Action Handlers) and Sprint 3.4 (Observability Services) required building collaborative adaptive cards and cost/audit/export services. Kroger had already defined all contracts and partial implementations in a parallel session.

## Decisions

### 1. Escalation persistence on split votes
Once a split vote (50/50) triggers an escalation reason, subsequent majority votes do NOT auto-clear it. The card stays in `Voting` lifecycle until explicit archive or escalation action. This prevents loss of governance context.

### 2. Council verdict → voting card integration
`CreateFromVerdictAsync` maps council agent decisions to card votes: `Approve`/`Conditional` → "approve", `Reject` → "reject". This auto-creates a voting card whenever the council convenes, making council decisions visible in the card UI.

### 3. ConversationExporter tracking via concrete method
`TrackMessageAsync` is a concrete method on `ConversationExporter`, not part of the `IConversationExport` interface. This keeps the contract focused on export capabilities while allowing pipeline-specific message tracking as an implementation detail.

### 4. Duplicate endpoint removal
Kroger registered a second set of card + observability endpoints in Program.cs. Removed the duplicate set (simpler, no validation/filtering) and kept Costco's set which includes input validation, query parameter filtering, and `ListAsync` support.

### 5. TrackedMessage as init-property record
Used `required init` properties instead of positional record parameters so the type works with object-initializer syntax in the chat pipeline (`new TrackedMessage { Role = "user", Content = msg }`).

## Impact

- All 1154 tests pass
- 12 new REST endpoints for cards + observability
- Chat pipeline now tracks cost, audit, and conversation export on every request
