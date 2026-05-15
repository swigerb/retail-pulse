# Security Hardening

> Security measures implemented in RetailPulse.

---

## Security Headers

All responses include these security headers (via `SecurityHeadersMiddleware`):

| Header | Value | Purpose |
|--------|-------|---------|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | Force HTTPS |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self' 'unsafe-inline'...` | Prevent XSS/injection |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking |
| `X-XSS-Protection` | `0` | Disabled (CSP is preferred) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Limit referrer leakage |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | Restrict browser APIs |

---

## Input Validation

All chat requests are validated by `ChatRequestValidator` before reaching the agent:

| Rule | Limit | Error |
|------|-------|-------|
| Message length | Max 2000 characters | 400 — "Message exceeds maximum length" |
| Message format | Non-empty, trimmed | 400 — "Message is required" |
| XSS detection | No `<script>`, `javascript:`, `on*=` patterns | 400 — "Message contains disallowed content" |
| Session ID format | Alphanumeric + hyphens only | 400 — "Invalid session ID format" |

Validation errors return RFC 7807 Problem Details with specific error descriptions.

---

## Audit Log

All chat interactions are recorded in a tamper-evident audit log (`DurableAuditLog`):

**Storage:** SQLite database (local file, production: managed storage)

**Fields per entry:**
- Timestamp (UTC)
- Correlation ID
- User identifier
- Action type
- Request summary (truncated)
- Response metadata
- SHA256 hash (current entry + previous hash)

**Tamper detection:** Each entry's hash incorporates the previous entry's hash, forming a hash chain. Any modification to historical entries breaks the chain and is detectable.

**Querying:** The audit log is append-only. Query by correlation ID, user, or time range for compliance reviews.

---

## Authentication

### Development
- `DevelopmentAuthHandler` bypasses all authentication
- `Security:RequireAuth` defaults to `false`
- No API key required

### Production
- **API Key:** Required via `x-api-key` header (configured in `ApiKey:Value`)
- **JWT Bearer:** For Teams bot integration (validated by `TeamsSsoHandler`)
- **Managed Identity:** For Azure OpenAI and APIM (no secrets in code)

---

## Rate Limiting

Four tiers of rate limiting (ASP.NET Core Rate Limiter):

| Policy | Limit | Applies To |
|--------|-------|-----------|
| `strict` | 10 req/min | `/api/v1/chat`, AI-intensive routes |
| `standard` | 30 req/min | General API endpoints |
| `relaxed` | 100 req/min | Health checks, static resources |
| `upload` | 5 req/min | File upload endpoints |

Rate limit responses include `Retry-After` header.

---

## OWASP Coverage

The test suite includes OWASP Top 10 validation:

| OWASP | Category | Test Coverage |
|-------|----------|---------------|
| A01 | Broken Access Control | API key validation, auth bypass prevention |
| A03 | Injection | XSS detection, input sanitization |
| A05 | Security Misconfiguration | Header validation, default credentials check |
| A07 | Authentication Failures | Token validation, session management |

See `tests/RetailPulse.Tests/Security/Owasp*.cs` for test implementations.

---

## Secrets Management

- **Never** committed to source control
- User secrets (`dotnet user-secrets`) for local development
- Azure Key Vault references for production deployments
- Managed Identity preferred over connection strings
- `.gitignore` excludes all secret files
