# ADR-003: Consensus Council — Fan-Out Voting Pattern

## Status

Accepted

## Context

Portfolio health assessments require input from multiple specialist perspectives — demand trends, promotional effectiveness, competitive positioning, supply chain status, and store operations. No single agent has the full picture. Users requesting "How healthy is Brand X?" need a synthesized, multi-dimensional answer.

## Decision

We implement a **Consensus Council** pattern (`IConsensusCouncil`) that:

1. **Fan-out**: When the router classifies intent as `council/health`, the system invokes multiple specialist agents in parallel, each providing their perspective on the queried brand/product.
2. **Vote collection**: Each agent returns a structured vote containing:
   - Agent ID and name
   - Rating (e.g., Healthy, At Risk, Critical)
   - Confidence score (0.0–1.0)
   - Reasoning text
3. **Synthesis**: A dedicated `council-synthesis` agent aggregates all votes into a unified verdict with:
   - Overall rating (majority or weighted)
   - Unanimity indicator
   - Key action items
   - Disagreement highlights
4. **Presentation**: Results are formatted as a structured response and an Adaptive Card (Voting type) for interactive display.

## Consequences

**Positive:**
- Provides genuinely multi-perspective health assessments rather than a single-agent view.
- Parallel execution means total latency is ~max(agent latencies) rather than sum.
- Disagreements between agents surface genuine business tensions (e.g., great demand but supply issues).
- Voting card allows stakeholders to add their own perspective.

**Negative:**
- Higher token consumption (N agents × prompt + response tokens).
- Synthesis quality depends on the council-synthesis prompt being well-tuned.
- Adds complexity to the routing layer (special-case interception for council intent).
- If one agent is slow, it delays the entire council response.
