# Squad Decisions

## Active Decisions

### Telemetry Total Duration (2026-05-04)

- **Context:** The backend `thought` span covers the full `GetResponseAsync()` wall-clock time, so summing span durations in the web telemetry drawer overstates total request time when tool calls are present.
- **Decision:** Expose `TotalDurationMs` on the shared `ChatResponse` contract and have the telemetry drawer prefer that response-level value, with a fallback to summed spans when the response-level value is absent.
- **Impact:** Individual span durations remain visible, the total duration display reflects real request time, and older clients/responses stay compatible through the fallback path.
- **Owner:** Costco (Backend Dev)

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
