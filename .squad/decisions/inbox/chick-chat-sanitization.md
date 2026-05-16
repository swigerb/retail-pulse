# Decision: Frontend chat message sanitization (defense-in-depth)

**Date:** 2026-05-15
**Author:** Chick (Frontend Dev)
**Status:** Implemented

## Context
Backend tool-call artifacts (`to=functions.IdentifyDemandRisks {...json}`) were leaking into rendered chat messages, including garbled Unicode characters.

## Decision
Added `src/utils/sanitizeMessage.ts` as a defense-in-depth layer that strips tool-call patterns before rendering in ChatPanel. This is NOT a replacement for backend fixing the root cause — it's a safety net.

## Patterns stripped:
- `to=functions.*` prefixes
- JSON payloads following tool-call markers
- Garbled CJK Unicode in tool-call context lines

## Impact on Costco (Backend)
- Backend should still fix the root cause of tool-call content leaking into response text
- Frontend sanitization means the demo is unblocked regardless of backend fix timeline
- If backend adds new tool-call patterns, the frontend regex may need updating

## Convention
All assistant message content should pass through `sanitizeMessage()` before rendering. Applied to both static and streaming message paths.
