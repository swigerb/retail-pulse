# 2026-06-03: Memory-management routing must fail closed on destructive intent only

**By:** Costco (Backend Dev)

## Decision
The `memory/management` router intent and `MemoryManagementAgent` should treat only explicit destructive phrases as clear/reset actions. Any message starting with `remember` must be handled as a store request if it reaches the memory-management agent, even if routing misclassifies it.

## Why
This bug showed that a single over-broad keyword or prompt description can turn a benign "remember that..." request into destructive data loss. The specialist now acts as a defense-in-depth layer so misrouting cannot wipe user memory.

## Impact
- Router keyword patterns and prompt wording must stay destructive-only.
- Future memory-management changes should preserve store-vs-clear discrimination in the specialist, not rely solely on routing.
- QA should treat "remember ..." as a regression case anywhere memory reset behavior is touched.
