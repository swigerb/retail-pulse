# Tool Enforcement in System Prompt (2026-05-07)

- **Context:** gpt-5.4-mini was responding to data/visualization requests with text-only responses, skipping available tools (GetPortfolioDepletionStats, CreateChart) entirely. The system prompt described tools but never mandated their use.
- **Decision:** Added a "Critical: Always Use Tools for Data Requests" section to `prompts.yaml` that (1) mandates tool calls for all data questions, (2) maps common business concepts to specific tools (e.g., "market share" → GetPortfolioDepletionStats), and (3) maps data types to chart types (e.g., proportional breakdown → pie chart). This section is placed BEFORE the visualization guidelines so the model encounters the mandate early.
- **Impact:** The model should now reliably call data tools first, then CreateChart for visualizations, instead of producing text-only responses. No C# or frontend changes needed — this is prompt engineering only.
- **Owner:** Costco (Backend Dev)
