// @ts-check
/**
 * Provider build/test matrix for the Retail Pulse SPA (Sprint 4, epic #27).
 *
 * Proves — with NO live secrets and only SAFE SYNTHETIC PUBLIC identifiers — that the
 * frontend build behaves correctly for every authentication mode:
 *
 *   Config gate (fast — drives scripts/validate-auth-config.mjs, the real `prebuild` guard):
 *     • Entra + valid synthetic tenant/client GUIDs   → PASS
 *     • Entra + MISSING tenant/client ids             → FAIL  (refuses to ship an empty Entra shell)
 *     • Entra + angle-bracket placeholder ids         → FAIL
 *     • Entra + empty-GUID ids                         → FAIL
 *     • GitHub  (mode only; no Entra ids required)    → PASS
 *     • Anonymous (mode only; no Entra ids required)  → PASS
 *     • Unknown mode (e.g. "Okta")                    → FAIL  (an unknown selector never picks a provider)
 *     • Unset mode (local dev / CI type-check build)  → PASS
 *
 *   Full build + auth-mode meta behavioral test (real `tsc -b` + `vite build`, synthetic env):
 *     • Builds Entra, GitHub, and Anonymous (all statically bundled — no secrets).
 *     • Inspects each EMITTED index.html with the production verifier's exact parser + predicate
 *       (auth-mode-meta.mjs): Entra carries meta `Entra` and PASSES isProductionEntra; GitHub and
 *       Anonymous carry their own normalized name and FAIL the production-Entra predicate.
 *     • --gate-only: skips the full builds entirely (fastest; gate matrix only).
 *     • --full: accepted for back-compat; all three modes always build for the meta proof.
 *
 * All identifiers are synthetic and public. No .env* file is read or written; every scenario's
 * environment is passed in-process. Output goes to ./dist-matrix/<mode> and is cleaned up.
 *
 * Exit code is non-zero if ANY scenario deviates from its expectation, so CI can gate on it.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, rmSync, readdirSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { parseAuthModeMeta, isProductionEntra, normalizeAuthMode } from './auth-mode-meta.mjs';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const MATRIX_OUT = join(webRoot, 'dist-matrix');

// Safe, synthetic, PUBLIC identifiers — not tied to any real tenant/app.
const SYN = {
  tenant: '11111111-1111-1111-1111-111111111111',
  client: '22222222-2222-2222-2222-222222222222',
  apiOrigin: 'https://retail-pulse-api.example.azurecontainerapps.io',
};

const argv = new Set(process.argv.slice(2));
const full = argv.has('--full');
const gateOnly = argv.has('--gate-only');

let failures = 0;
const report = (ok, name, detail = '') => {
  if (ok) {
    console.log(`  [PASS] ${name}`);
  } else {
    console.error(`  [FAIL] ${name}${detail ? ' — ' + detail : ''}`);
    failures++;
  }
};

/** Build a deterministic child env: strip ambient VITE_* then apply the scenario. */
function scenarioEnv(env) {
  /** @type {Record<string,string|undefined>} */
  const base = { ...process.env };
  for (const k of Object.keys(base)) {
    if (k.startsWith('VITE_')) delete base[k];
  }
  return { ...base, ...env };
}

function run(cmd, args, env, useShell = false) {
  return spawnSync(cmd, args, {
    cwd: webRoot,
    env: scenarioEnv(env),
    encoding: 'utf8',
    shell: useShell && process.platform === 'win32',
  });
}

// ── Config-gate matrix ───────────────────────────────────────────────────────
const gateScenarios = [
  { name: 'Entra + valid synthetic ids → PASS', expectPass: true,
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client } },
  { name: 'Entra + MISSING ids → FAIL', expectPass: false,
    env: { VITE_AUTH_MODE: 'Entra' } },
  { name: 'Entra + placeholder ids → FAIL', expectPass: false,
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: '<tenant-id>', VITE_ENTRA_CLIENT_ID: '<client-id>' } },
  { name: 'Entra + empty-GUID ids → FAIL', expectPass: false,
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: '00000000-0000-0000-0000-000000000000', VITE_ENTRA_CLIENT_ID: '00000000-0000-0000-0000-000000000000' } },
  { name: 'GitHub (mode only) → PASS', expectPass: true,
    env: { VITE_AUTH_MODE: 'GitHub', VITE_API_ORIGIN: SYN.apiOrigin } },
  { name: 'Anonymous (mode only) → PASS', expectPass: true,
    env: { VITE_AUTH_MODE: 'Anonymous', VITE_API_ORIGIN: SYN.apiOrigin } },
  { name: 'Unknown mode "Okta" → FAIL', expectPass: false,
    env: { VITE_AUTH_MODE: 'Okta' } },
  { name: 'Unset mode (local dev / type-check) → PASS', expectPass: true,
    env: {} },
];

console.log('=== Frontend provider config-gate matrix ===');
for (const s of gateScenarios) {
  const res = run(process.execPath, [join('scripts', 'validate-auth-config.mjs')], s.env);
  const passed = res.status === 0;
  report(passed === s.expectPass, s.name,
    passed === s.expectPass ? '' : `gate exit=${res.status} (${(res.stderr || res.stdout || '').trim().split('\n').pop()})`);
}

// ── Full build matrix + immutable auth-mode meta behavioral test ───────────────
// All three providers are STATICALLY BUNDLED, so a JS "string absence" scan can never prove
// which mode is active. Instead every mode's real vite build bakes an immutable, normalized
// `<meta name="retail-pulse-auth-mode" content="...">` into index.html. We build Entra, GitHub,
// and Anonymous and inspect the EMITTED index.html with the exact same parser + predicate the
// production verifier uses (scripts/auth-mode-meta.mjs, mirrored in Verify-ProductionAuth.ps1):
//   • Entra      → meta content 'Entra'      and isProductionEntra === true  (verifier PASSES)
//   • GitHub     → meta content 'GitHub'     and isProductionEntra === false (verifier FAILS)
//   • Anonymous  → meta content 'Anonymous'  and isProductionEntra === false (verifier FAILS)
if (!gateOnly) {
  const buildModes = [
    { mode: 'entra', rawMode: 'Entra', env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client, VITE_API_ORIGIN: SYN.apiOrigin } },
    { mode: 'github', rawMode: 'GitHub', env: { VITE_AUTH_MODE: 'GitHub', VITE_API_ORIGIN: SYN.apiOrigin } },
    { mode: 'anonymous', rawMode: 'Anonymous', env: { VITE_AUTH_MODE: 'Anonymous', VITE_API_ORIGIN: SYN.apiOrigin } },
  ];
  void full; // retained for CLI back-compat; all three modes always build for the meta proof.

  console.log(`\n=== Frontend full build matrix + auth-mode meta behavioral test (${buildModes.map((b) => b.mode).join(', ')}) ===`);

  // Type-check once — vite build does not type-check, so mirror `npm run build`'s `tsc -b`.
  const tsc = run('npx', ['tsc', '-b'], buildModes[0].env, true);
  report(tsc.status === 0, 'tsc -b (type-check) succeeds',
    tsc.status === 0 ? '' : (tsc.stderr || tsc.stdout || '').trim().split('\n').slice(-3).join(' | '));

  if (tsc.status === 0) {
    for (const b of buildModes) {
      const outDir = join('dist-matrix', b.mode);
      // The prebuild gate must pass for a real build, so run it first (mirrors npm run build).
      const gate = run(process.execPath, [join('scripts', 'validate-auth-config.mjs')], b.env);
      if (gate.status !== 0) {
        report(false, `${b.mode}: prebuild gate`, (gate.stderr || gate.stdout || '').trim());
        continue;
      }
      const built = run('npx', ['vite', 'build', '--outDir', outDir, '--emptyOutDir'], b.env, true);
      const emitted = built.status === 0 && existsSync(join(MATRIX_OUT, b.mode)) &&
        readdirSync(join(MATRIX_OUT, b.mode)).length > 0;
      report(emitted, `${b.mode} build succeeds and emits a bundle`,
        emitted ? '' : (built.stderr || built.stdout || '').trim().split('\n').slice(-3).join(' | '));
      if (!emitted) continue;

      // Behavioral proof: inspect the EMITTED index.html with the verifier's parser + predicate.
      const htmlPath = join(MATRIX_OUT, b.mode, 'index.html');
      const html = existsSync(htmlPath) ? readFileSync(htmlPath, 'utf8') : '';
      const content = parseAuthModeMeta(html);
      const expected = normalizeAuthMode(b.rawMode);
      const metaPresent = content !== null;
      report(metaPresent && content === expected,
        `${b.mode}: emitted index.html carries immutable auth-mode meta = '${expected}'`,
        metaPresent ? `got '${content}'` : 'meta tag missing');

      const prodEntra = isProductionEntra(content);
      if (b.mode === 'entra') {
        report(prodEntra, `${b.mode}: satisfies the production-Entra verifier predicate`,
          prodEntra ? '' : `isProductionEntra('${content}') was false`);
      } else {
        report(!prodEntra, `${b.mode}: FAILS the production-Entra verifier predicate (as required)`,
          prodEntra ? `isProductionEntra('${content}') unexpectedly true` : '');
      }
    }
  }
}

// ── cleanup ───────────────────────────────────────────────────────────────────
try {
  if (existsSync(MATRIX_OUT)) rmSync(MATRIX_OUT, { recursive: true, force: true });
} catch { /* best effort */ }

console.log('');
if (failures === 0) {
  console.log('Provider build matrix: ALL SCENARIOS PASSED.');
  process.exit(0);
} else {
  console.error(`Provider build matrix: ${failures} scenario(s) FAILED.`);
  process.exit(1);
}

