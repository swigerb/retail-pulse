---
name: "brand-assets"
description: "Keep Retail Pulse branding consistent across README docs and the web app"
domain: "frontend-branding"
confidence: "high"
source: "observed during logo rollout"
---

## Context
Use this skill when updating Retail Pulse logos, favicons, or other shared brand imagery. The project presents the same identity in the repo README and the Vite frontend, so brand assets need to land in both places.

## Patterns
- Store README-facing image assets in `docs\` so markdown can reference tracked files in the repo.
- Store runtime brand assets in `src\RetailPulse.Web\public\` so the Vite app can serve them from root-relative URLs.
- Keep `src\RetailPulse.Web\src\components\BrandLogo.tsx` pointed at the shipped public asset instead of rebuilding the wordmark in JSX.
- Refresh `src\RetailPulse.Web\public\favicon.svg` with a simplified brand mark that stays legible at 16px–32px.
- Validate branding changes with `cd src\RetailPulse.Web && npm run build` and `cd src\RetailPulse.Web && npx vitest run`.

## Examples
- Official logo image copied to both `docs\retail-pulse-logo.jpg` and `src\RetailPulse.Web\public\retail-pulse-logo.jpg`.
- README hero uses the `docs\` asset while UI components load `/retail-pulse-logo.jpg` from the public folder.

## Anti-Patterns
- Updating the README logo without updating the web app asset copy.
- Drawing ad-hoc initials in JSX when the official logo asset already exists.
- Shipping a detailed favicon that becomes unreadable at small sizes.
