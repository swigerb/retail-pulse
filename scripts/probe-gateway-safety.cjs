#!/usr/bin/env node
/*
 * Live probe for APIM gateway content safety (#331). Read-only from the gateway's point of view: it only sends
 * chat requests through APIM and judges the sanitized x-rp-gw-safety-* evidence headers.
 *
 * It NEVER prints prompts, completions or response bodies. Trigger prompts come only from environment variables.
 *
 *   APIM_BASE_URL=https://<apim>.azure-api.net/inference/openai  DEPLOYMENT=<deployment>  API_VERSION=<ver>
 *   APIM_SUBSCRIPTION_KEY=...
 *   PROBE_BENIGN_PROMPT=... PROBE_ATTACK_PROMPT=... PROBE_SEV_BELOW_PROMPT=... PROBE_SEV_AT_PROMPT=...
 *   PROBE_OUTBOUND_BLOCK_PROMPT=...      # prompt that makes the model emit content at/above the threshold
 *   SAFETY_THRESHOLD=4
 *
 *   node scripts/probe-gateway-safety.cjs                       # benign, attack, boundary, outbound, streaming
 *   node scripts/probe-gateway-safety.cjs --scenario disabled   # deployment WITHOUT gateway safety: no safety evidence
 *   node scripts/probe-gateway-safety.cjs --scenario fault --fault 408|429|500|503|null|malformed|auth|config --mode demo|regulated
 *        (the operator puts the deployment in the named faulted state first; the probe asserts the outcome)
 *   node scripts/probe-gateway-safety.cjs --self-test           # offline: judges synthetic gateways, no Azure
 *
 * Exit 0 pass, 1 fail, 2 skipped (missing configuration). A skipped or self-test run is NOT live verification.
 */
'use strict';

const SAFETY = ['phase', 'decision', 'prompt-attack', 'category', 'severity', 'mode', 'latency-ms', 'output-checked'];
const H = (h, n) => h.get(`x-rp-gw-safety-${n}`);
const BACKEND_EVIDENCE = ['x-rp-gw-backend', 'x-rp-gw-backend-latency-ms', 'x-rp-gw-tokens-consumed', 'x-rp-gw-tokens-remaining'];
const hasBackendEvidence = (h) => BACKEND_EVIDENCE.some((n) => h.has(n));
const FAIL_OPEN = new Set(['408', '429', '500', '502', '503', '504']);

// Each judge returns a list of failed check names for a {status, headers} response. headers is a Map.
const judges = {
  disabled: (r) => {
    const bad = [];
    if (r.status !== 200) bad.push('disabled: ordinary traffic must succeed (200)');
    if (SAFETY.some((n) => r.headers.has(`x-rp-gw-safety-${n}`)) || r.headers.get('x-rp-gw-safety') === 'blocked') bad.push('disabled: no safety evidence may appear');
    return bad;
  },
  benign: (r) => {
    const bad = [];
    if (r.status !== 200) bad.push('benign: expected 200');
    if (H(r.headers, 'decision') !== 'allow') bad.push('benign: decision must be allow');
    if (H(r.headers, 'prompt-attack') !== 'false') bad.push('benign: prompt-attack must be false');
    if (H(r.headers, 'output-checked') !== 'true') bad.push('benign: output must be checked');
    return bad;
  },
  attack: (r) => {
    const bad = [];
    if (r.status !== 403) bad.push('attack: expected 403');
    if (H(r.headers, 'decision') !== 'block' || H(r.headers, 'phase') !== 'input') bad.push('attack: expected input block');
    if (H(r.headers, 'prompt-attack') !== 'true') bad.push('attack: prompt-attack must be true');
    if (hasBackendEvidence(r.headers)) bad.push('attack: backend must not be invoked (no backend/token evidence)');
    return bad;
  },
  below: (r) => {
    const bad = [];
    if (r.status !== 200 || H(r.headers, 'decision') !== 'allow') bad.push('boundary-below: severity under threshold must be allowed');
    return bad;
  },
  at: (r, cfg) => {
    const bad = [];
    const sev = Number(H(r.headers, 'severity'));
    if (r.status !== 403 || H(r.headers, 'decision') !== 'block') bad.push('boundary-at: severity at threshold must block');
    if (!(sev >= cfg.threshold)) bad.push('boundary-at: reported severity must be >= threshold');
    if (hasBackendEvidence(r.headers)) bad.push('boundary-at: backend must not be invoked');
    return bad;
  },
  outbound: (r) => {
    const bad = [];
    if (r.status !== 403 || H(r.headers, 'decision') !== 'block' || H(r.headers, 'phase') !== 'output') bad.push('outbound: expected output-phase block (403)');
    return bad;
  },
  streaming: (r) => {
    const bad = [];
    if (r.status !== 200) bad.push('streaming: expected 200');
    if (H(r.headers, 'decision') !== 'allow') bad.push('streaming: input decision must be allow');
    // Truth: a stream cannot be output-checked, so the gateway must say so rather than claim a full pass.
    if (H(r.headers, 'output-checked') !== 'false') bad.push('streaming: output-checked must be false (input-only)');
    return bad;
  },
  fault: (r, cfg) => {
    const bad = [];
    const failOpen = cfg.mode === 'demo' && FAIL_OPEN.has(cfg.fault);
    if (failOpen) {
      if (r.status !== 200 || H(r.headers, 'decision') !== 'allowed_on_safety_error') bad.push(`fault-${cfg.fault}: demo must fail open with allowed_on_safety_error`);
      if (H(r.headers, 'output-checked') === 'true') bad.push(`fault-${cfg.fault}: a safety error must never claim output checked`);
    } else {
      if (r.status !== 503) bad.push(`fault-${cfg.fault}/${cfg.mode}: must fail closed with 503`);
      if (H(r.headers, 'decision') === 'allow' || H(r.headers, 'decision') === 'allowed_on_safety_error') bad.push(`fault-${cfg.fault}/${cfg.mode}: must not allow`);
      if (hasBackendEvidence(r.headers)) bad.push(`fault-${cfg.fault}/${cfg.mode}: backend must not be invoked`);
    }
    return bad;
  },
};

function report(results) {
  let failed = 0;
  for (const { name, bad } of results) {
    if (bad.length === 0) console.log(`  [ok]   ${name}`);
    else { failed++; console.log(`  [FAIL] ${name}`); bad.forEach((b) => console.log(`         - ${b}`)); }
  }
  return failed;
}

async function send(cfg, prompt, stream) {
  const url = `${cfg.baseUrl}/deployments/${cfg.deployment}/chat/completions?api-version=${cfg.apiVersion}`;
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'api-key': cfg.subscriptionKey, 'content-type': 'application/json' },
    body: JSON.stringify({ messages: [{ role: 'user', content: prompt }], max_tokens: 16, stream }),
  });
  await res.arrayBuffer(); // drained and discarded; bodies are never read or printed
  return { status: res.status, headers: new Map([...res.headers.entries()].map(([k, v]) => [k.toLowerCase(), v])) };
}

function parseArgs(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) if (argv[i].startsWith('--')) o[argv[i].slice(2)] = argv[i + 1]?.startsWith('--') || argv[i + 1] === undefined ? true : argv[++i];
  return o;
}

async function live(args) {
  const e = process.env;
  const cfg = { baseUrl: e.APIM_BASE_URL, deployment: e.DEPLOYMENT, apiVersion: e.API_VERSION, subscriptionKey: e.APIM_SUBSCRIPTION_KEY, threshold: Number(e.SAFETY_THRESHOLD || 4), mode: args.mode || 'demo', fault: String(args.fault || '') };
  const scenario = args.scenario || 'all';
  const prompts = { benign: e.PROBE_BENIGN_PROMPT, attack: e.PROBE_ATTACK_PROMPT, below: e.PROBE_SEV_BELOW_PROMPT, at: e.PROBE_SEV_AT_PROMPT, outbound: e.PROBE_OUTBOUND_BLOCK_PROMPT };
  const plan = scenario === 'disabled' ? [['disabled', 'disabled', prompts.benign, false]]
    : scenario === 'fault' ? [['fault', 'fault', prompts.benign, false]]
    : [['benign', 'benign', prompts.benign, false], ['attack', 'attack', prompts.attack, false], ['boundary-below', 'below', prompts.below, false], ['boundary-at', 'at', prompts.at, false], ['outbound-block', 'outbound', prompts.outbound, false], ['streaming-input-only', 'streaming', prompts.benign, true]];
  const missing = ['baseUrl', 'deployment', 'apiVersion', 'subscriptionKey'].filter((k) => !cfg[k]).concat(plan.filter(([, , p]) => !p).map(([n]) => `prompt for ${n}`));
  if (missing.length) { console.log(`SKIP: missing configuration (${missing.join(', ')}). Nothing was probed.`); return 2; }
  if (scenario === 'fault' && !cfg.fault) { console.log('SKIP: --fault is required for --scenario fault.'); return 2; }
  const results = [];
  for (const [name, judge, prompt, stream] of plan) {
    try { results.push({ name, bad: judges[judge](await send(cfg, prompt, stream), cfg) }); }
    catch (err) { results.push({ name, bad: [`request failed (${err && err.name ? err.name : 'error'})`] }); }
  }
  const failed = report(results);
  console.log(failed ? `FAIL ${failed}/${results.length}` : `PASS ${results.length}/${results.length} (live evidence headers only)`);
  return failed ? 1 : 0;
}

function selfTest() {
  const R = (status, o = {}) => ({ status, headers: new Map(Object.entries(o)) });
  const sh = (o) => Object.fromEntries(Object.entries(o).map(([k, v]) => [`x-rp-gw-safety-${k}`, v]));
  const backend = { 'x-rp-gw-backend': 'primary-be', 'x-rp-gw-tokens-consumed': '10' };
  const cfg = { threshold: 4 };
  const cases = [
    // [name, judge, response, cfg, expectPass]
    ['disabled:correct', 'disabled', R(200, backend), cfg, true],
    ['disabled:leaks-safety-evidence', 'disabled', R(200, sh({ decision: 'allow' })), cfg, false],
    ['benign:correct', 'benign', R(200, { ...backend, ...sh({ decision: 'allow', 'prompt-attack': 'false', 'output-checked': 'true' }) }), cfg, true],
    ['benign:unchecked-output-claimed-pass', 'benign', R(200, sh({ decision: 'allow', 'prompt-attack': 'false', 'output-checked': 'false' })), cfg, false],
    ['attack:correct', 'attack', R(403, sh({ decision: 'block', phase: 'input', 'prompt-attack': 'true' })), cfg, true],
    ['attack:backend-was-invoked', 'attack', R(403, { ...backend, ...sh({ decision: 'block', phase: 'input', 'prompt-attack': 'true' }) }), cfg, false],
    ['attack:allowed', 'attack', R(200, sh({ decision: 'allow', 'prompt-attack': 'false' })), cfg, false],
    ['boundary-below:correct', 'below', R(200, sh({ decision: 'allow' })), cfg, true],
    ['boundary-at:correct', 'at', R(403, sh({ decision: 'block', severity: '4' })), cfg, true],
    ['boundary-at:allowed-at-threshold', 'at', R(200, sh({ decision: 'allow', severity: '4' })), cfg, false],
    ['outbound:correct', 'outbound', R(403, sh({ decision: 'block', phase: 'output' })), cfg, true],
    ['outbound:input-phase', 'outbound', R(403, sh({ decision: 'block', phase: 'input' })), cfg, false],
    ['streaming:correct-input-only', 'streaming', R(200, sh({ decision: 'allow', phase: 'input', 'output-checked': 'false' })), cfg, true],
    ['streaming:claims-output-checked', 'streaming', R(200, sh({ decision: 'allow', 'output-checked': 'true' })), cfg, false],
    ...['408', '429', '500', '503'].flatMap((f) => [
      [`fault-${f}:demo-fail-open`, 'fault', R(200, sh({ decision: 'allowed_on_safety_error', 'output-checked': 'false' })), { mode: 'demo', fault: f }, true],
      [`fault-${f}:demo-blocked-wrongly`, 'fault', R(503, sh({ decision: 'block' })), { mode: 'demo', fault: f }, false],
      [`fault-${f}:regulated-fail-closed`, 'fault', R(503, sh({ decision: 'block' })), { mode: 'regulated', fault: f }, true],
      [`fault-${f}:regulated-allowed`, 'fault', R(200, sh({ decision: 'allowed_on_safety_error' })), { mode: 'regulated', fault: f }, false],
    ]),
    ...['null', 'malformed', 'auth', 'config'].flatMap((f) => [
      [`fault-${f}:demo-fail-closed`, 'fault', R(503, sh({ decision: 'block' })), { mode: 'demo', fault: f }, true],
      [`fault-${f}:demo-fail-open-is-wrong`, 'fault', R(200, sh({ decision: 'allowed_on_safety_error' })), { mode: 'demo', fault: f }, false],
      [`fault-${f}:backend-invoked`, 'fault', R(503, { ...backend, ...sh({ decision: 'block' }) }), { mode: 'regulated', fault: f }, false],
    ]),
  ];
  const results = cases.map(([name, judge, resp, c, expectPass]) => {
    const bad = judges[judge](resp, c);
    return { name, bad: (bad.length === 0) === expectPass ? [] : [expectPass ? `expected pass but failed: ${bad[0]}` : 'broken gateway was accepted'] };
  });
  const failed = report(results);
  console.log(failed ? `SELFTEST FAIL ${failed}` : `SELFTEST PASS ${results.length} cases (offline; NOT live APIM verification)`);
  return failed ? 1 : 0;
}

if (require.main === module) {
  const args = parseArgs(process.argv.slice(2));
  Promise.resolve(args['self-test'] ? selfTest() : live(args)).then((c) => process.exit(c), () => process.exit(1));
}
module.exports = { judges };
