/**
 * Defense-in-depth sanitization for chat messages.
 * Strips internal tool-call artifacts that may leak from the backend
 * before content is rendered to the user.
 */

// Matches `to=functions.FunctionName` patterns (with optional surrounding whitespace)
const TOOL_CALL_PREFIX_RE = /to=functions\.\w+\s*/g;

// Matches raw JSON-like function call blocks: `{"key":"value"...}` preceded by tool markers
// Only strip when preceded by a tool call pattern on the same logical block
const TOOL_CALL_BLOCK_RE = /^to=functions\.\w+[^\n]*?\{[\s\S]*?\}\s*/gm;

// Matches garbled Unicode text commonly seen in tool-call leakage (CJK characters mixed with latin in tool context)
// Captures the entire line including any trailing JSON block
const GARBLED_TOOL_CONTEXT_RE = /[\u4e00-\u9fff\u3400-\u4dbf]{2,}[^\n]*?(json|functions)[^\n]*\n*/gi;

/**
 * Sanitizes an assistant message by removing tool-call artifacts.
 * Returns the cleaned string with leading/trailing whitespace trimmed.
 */
export function sanitizeMessage(content: string): string {
  if (!content) return content;

  let cleaned = content;

  // Remove full tool-call blocks (to=functions.X ... {json})
  cleaned = cleaned.replace(TOOL_CALL_BLOCK_RE, '');

  // Remove any remaining `to=functions.*` fragments
  cleaned = cleaned.replace(TOOL_CALL_PREFIX_RE, '');

  // Remove garbled Unicode + tool context lines
  cleaned = cleaned.replace(GARBLED_TOOL_CONTEXT_RE, '');

  // Collapse multiple blank lines into one
  cleaned = cleaned.replace(/\n{3,}/g, '\n\n');

  return cleaned.trim();
}
