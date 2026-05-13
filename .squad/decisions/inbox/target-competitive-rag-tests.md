# Decision: Sprint 2.2+2.3 Test Strategy — Competitive Intel + RAG

**Author:** Target (Tester)
**Date:** 2026-05-15
**Status:** Implemented

## Context

Sprint 2.2 (Competitive Intelligence Agent) and Sprint 2.3 (RAG Knowledge Base) required comprehensive test coverage. Both features had significant implementation already in the codebase, requiring tests to align with actual APIs rather than assumed contracts.

## Decisions

### 1. Competitive alert tests use existing alert types

**Decision:** Competitive alert tests reuse the 3 existing alert types (`demand_spike`, `supply_drop`, `trend_reversal`) framed in competitive scenarios, rather than introducing new `competitor_price_drop` or `market_share_loss` types.

**Rationale:** `InMemoryAlertService` only recognizes 3 types — unknown types return deviation=0 and never fire. Adding new types would require modifying the service's switch expression, which is backend team (Costco) scope.

### 2. RAG tests written against actual API surface

**Decision:** RAG test files were rewritten to match the real `InMemoryKnowledgeBase` API (requires `ILogger`, BM25 scoring, specific record types) rather than assumed contracts from the task spec.

**Rationale:** The RAG implementation existed but was uncommitted. Tests must match real code to provide regression value. Key differences: `DocumentChunker` is static, `InMemoryKnowledgeBase` requires logger injection, no `GetStatsAsync` method exists.

### 3. MessageExtensionTests are test-first contracts

**Decision:** `MessageExtensionTests.cs` defines a test-first contract for a future Teams message extension handler that doesn't exist yet. Tests mock `IKnowledgeBase` and validate expected behavior patterns.

**Rationale:** Sprint 2.3 scope includes Teams integration. Test-first contracts guide implementation while providing immediate validation of the `IKnowledgeBase` interface from a consumer perspective.

### 4. RAG source files committed alongside tests

**Decision:** Committed `InMemoryKnowledgeBase.cs`, `DocumentChunker.cs`, `RagContextProvider.cs`, `KnowledgeBaseSeeder.cs`, `IKnowledgeBase.cs`, and sample docs in the same commit as tests.

**Rationale:** These files existed in the working tree from a prior agent session but were never committed. Tests depend on them — committing together ensures the commit is self-contained and buildable.

## Impact

- 164 new tests across 9 files, 803 total passing
- Competitive intel agent has full test coverage matching existing backend implementation
- RAG knowledge base has test coverage for BM25 search, chunking, and provider patterns
- No changes to existing source files (except RouterIntegrationTests.cs which gained 5 tests)
