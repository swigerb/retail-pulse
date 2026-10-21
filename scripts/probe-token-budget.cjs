#!/usr/bin/env node
/*
 * Live probe for per-scope token budgets and the operator quota demo (#332).
 *
 * Checks, in order: disabled/normal mode, normal versus tiny limit, deterministic HTTP 429 with a numeric
 * Retry-After, no fabricated consumed tokens on the pre-backend 429, isolated scopes, immediate reset through
 * the demo counter generation, spoof resistance, secret hygiene, and the gateway-local/reset documentation.
 * A long-period token-quota breach is a 403 and is deliberately NOT what this probe asserts: the TPM demo is 429.
 *
 * Secrets come only from the environment and are never printed:
 *   APIM_BASE_URL=https://<apim>.azure-api.net/inference/openai  DEPLOYMENT=<deployment>  API_VERSION=<ver>
 *   KEY_SCOPE_A=<subscription key of scope A>  EXPECT_SCOPE_A=store|channel|application|b2b
 *   KEY_SCOPE_B=<subscription key of a different scope>  EXPECT_SCOPE_B=...
 *   TOKEN_BUDGET_DEMO=disabled        # deployment without tokenBudgetDemoEnabled: only normal-mode checks run
 *   -- or --
 *   APIM_NAME=<apim> RESOURCE_GROUP=<rg>   # enabled mode; arms/bumps via `az apim nv update` (rp-budget-demo-*)
 *
 * node scripts/probe-token-budget.cjs --self-test   runs the harness against an in-process fake gateway (no Azure)
 *   and proves it passes a correct gateway and fails broken ones. Exit 0 pass, 1 fail, 2 skipped (missing config).
 * node scripts/probe-token-budget.cjs --test-az-launcher   validates the local Azure CLI launch path without mutations.
 */
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

const SPOOF = {
  'x-rp-budget-scope': 'b2b',
  'x-rp-budget-demo': 'true',
  'x-rp-gw-budget-scope': 'b2b',
  'x-rp-gw-budget-mode': 'demo',
  'x-rp-gw-retry-after': '1',
  'x-rp-gw-tokens-consumed': '999999',
  'x-rp-gw-quota': 'allowed',
};
const KINDS = ['store', 'channel', 'application', 'b2b'];
const NUMERIC = /^[0-9]{1,9}$/;
let cleanupControl = null;
let shutdownRequested = false;
let signalExitCode = null;
let activeRequestController = null;
let activeAzChild = null;

async function disarmDemo() {
  if (!cleanupControl) return true;
  const control = cleanupControl;
  try {
    await control.arm(false);
    cleanupControl = null;
    return true;
  } catch (err) {
    console.error(`WARNING: failed to disarm token budget demo: ${err.message}`);
    return false;
  }
}

async function finalizeExitCode(code) {
  if (!await disarmDemo()) {
    console.error('ERROR: token budget demo may still be ARMED; verify rp-budget-demo-armed before retrying.');
    return signalExitCode ?? 1;
  }
  return signalExitCode ?? code;
}

function requestShutdown(code) {
  if (shutdownRequested) return;
  shutdownRequested = true;
  signalExitCode = code;
  activeRequestController?.abort();
  terminateProcessTree(activeAzChild);
}

for (const [signal, code] of [['SIGINT', 130], ['SIGTERM', 143]]) {
  process.on(signal, () => requestShutdown(code));
}

function throwIfShuttingDown() {
  if (shutdownRequested) throw new Error('probe interrupted');
}

async function post(cfg, key, headers = {}) {
  const url = `${cfg.baseUrl}/deployments/${cfg.deployment}/chat/completions?api-version=${cfg.apiVersion}`;
  const controller = new AbortController();
  activeRequestController = controller;
  const timer = setTimeout(() => controller.abort(), cfg.requestTimeoutMs || 30000);
  if (shutdownRequested) controller.abort();
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'api-key': key, 'content-type': 'application/json', ...headers },
      body: JSON.stringify({ messages: [{ role: 'user', content: 'ping' }], max_completion_tokens: 32 }),
      signal: controller.signal,
    });
    const body = await res.text();
    return { status: res.status, headers: res.headers, body };
  } finally {
    clearTimeout(timer);
    if (activeRequestController === controller) activeRequestController = null;
  }
}

function leaks(r, keys) {
  const all = r.body + [...r.headers.entries()].map(([k, v]) => `${k}:${v}`).join('\n');
  return keys.some((k) => k && all.includes(k));
}

async function exhaust(cfg, key, max = 30) {
  let last;
  for (let i = 0; i < max; i++) {
    last = await post(cfg, key);
    if (last.status === 429) return last;
  }
  return last;
}

function docChecks(check) {
  const root = path.resolve(__dirname, '..');
  const p = path.join(root, 'docs', 'token-budgets.md');
  const doc = fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : '';
  check('docs: gateway-local counters documented', /gateway-local/i.test(doc) && /region/i.test(doc), 'docs/token-budgets.md');
  check('docs: demo reset generation documented', /rp-budget-demo-generation/.test(doc) && /clear-counter/i.test(doc), 'docs/token-budgets.md');
  check('docs: normal Retry-After reset documented', /Retry-After/.test(doc), 'docs/token-budgets.md');
  check('docs: quota 403 distinguished from TPM 429', /403/.test(doc) && /429/.test(doc), 'docs/token-budgets.md');
}

async function runDisabledProbe(cfg) {
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail });
  const keys = [cfg.keyA, cfg.keyB];

  let r = await post(cfg, cfg.keyA);
  check('disabled: normal traffic succeeds', r.status >= 200 && r.status < 300, `status ${r.status}`);
  check('disabled: mode is normal', r.headers.get('x-rp-gw-budget-mode') === 'normal', `mode ${r.headers.get('x-rp-gw-budget-mode')}`);
  check('disabled: scope kind matches subscription product', r.headers.get('x-rp-gw-budget-scope') === cfg.expectA, `scope ${r.headers.get('x-rp-gw-budget-scope')}`);
  check('disabled: no key material returned', !leaks(r, keys), 'key leaked');

  r = await post(cfg, cfg.keyA, SPOOF);
  check('disabled: spoofed headers do not change scope', r.headers.get('x-rp-gw-budget-scope') === cfg.expectA, `scope ${r.headers.get('x-rp-gw-budget-scope')}`);
  check('disabled: spoofed headers cannot force demo mode', r.headers.get('x-rp-gw-budget-mode') === 'normal', 'demo mode reflected');
  check('disabled: spoofed headers do not throttle', r.status >= 200 && r.status < 300, `status ${r.status}`);
  check('disabled: spoofed consumed is not reflected', r.headers.get('x-rp-gw-tokens-consumed') !== '999999', 'spoofed consumed reflected');
  docChecks(check);
  return results;
}

async function runEnabledProbe(cfg, control) {
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail });
  const keys = [cfg.keyA, cfg.keyB];

  throwIfShuttingDown();
  await control.arm(false);
  throwIfShuttingDown();
  let r = await post(cfg, cfg.keyA);
  check('normal: admitted under production limit', r.status >= 200 && r.status < 300, `status ${r.status}`);
  check('normal: no key material returned', !leaks(r, keys), 'key leaked');
  check('normal: mode normal, scope kind matches', r.headers.get('x-rp-gw-budget-mode') === 'normal' && r.headers.get('x-rp-gw-budget-scope') === cfg.expectA, 'mode/scope mismatch');

  r = await post(cfg, cfg.keyA, SPOOF);
  check('spoof: caller cannot force scope or demo mode', r.headers.get('x-rp-gw-budget-scope') === cfg.expectA && r.headers.get('x-rp-gw-budget-mode') === 'normal', 'spoofed budget evidence reflected');
  check('spoof: caller cannot throttle or inject retry', r.status < 300 && !r.headers.has('retry-after'), `status ${r.status}`);

  await control.bump();
  throwIfShuttingDown();
  await control.arm(true);
  throwIfShuttingDown();
  const tiny = await exhaust(cfg, cfg.keyA);
  check('demo: tiny limit yields HTTP 429 (not the 403 long-period quota)', tiny.status === 429, `status ${tiny.status}`);
  check('demo: Retry-After is numeric', NUMERIC.test(tiny.headers.get('retry-after') || ''), `retry-after ${tiny.headers.get('retry-after')}`);
  check('demo: gateway evidence carries numeric retry', NUMERIC.test(tiny.headers.get('x-rp-gw-retry-after') || ''), 'x-rp-gw-retry-after missing/non-numeric');
  check('demo: quota is throttled', tiny.headers.get('x-rp-gw-quota') === 'throttled', `quota ${tiny.headers.get('x-rp-gw-quota')}`);
  check('demo: mode demo and scope kind reported', tiny.headers.get('x-rp-gw-budget-mode') === 'demo' && tiny.headers.get('x-rp-gw-budget-scope') === cfg.expectA, 'mode/scope mismatch');
  const rem = tiny.headers.get('x-rp-gw-tokens-remaining');
  check('demo: remaining is numeric when the limiter publishes it', rem === null || NUMERIC.test(rem), `remaining ${rem}`);
  check('demo: no consumed tokens fabricated on the pre-backend 429', !tiny.headers.has('x-rp-gw-tokens-consumed') && !tiny.headers.has('x-tokens-consumed'), 'consumed present on 429');
  check('demo: no key material in 429', !leaks(tiny, keys), 'key leaked');

  const other = await post(cfg, cfg.keyB);
  check('isolation: another scope is not throttled by scope A', other.status !== 429 && other.status < 300, `status ${other.status}`);
  check('isolation: other scope reports its own kind', other.headers.get('x-rp-gw-budget-scope') === cfg.expectB, `scope ${other.headers.get('x-rp-gw-budget-scope')}`);

  const again = await post(cfg, cfg.keyA);
  check('demo: still throttled before reset (same generation)', again.status === 429, `status ${again.status}`);
  await control.bump();
  throwIfShuttingDown();
  const reset = await post(cfg, cfg.keyA);
  check('reset: bumping the generation admits scope A immediately', reset.status >= 200 && reset.status < 300, `status ${reset.status}`);

  await control.arm(false);
  throwIfShuttingDown();
  const after = await post(cfg, cfg.keyA);
  check('disarm: normal limit restored', after.status < 300 && after.headers.get('x-rp-gw-budget-mode') === 'normal', `status ${after.status}`);
  docChecks(check);
  return results;
}

function assertSafeCmdArg(value) {
  const text = String(value);
  if (/[&|<>^%!"\r\n]/.test(text)) throw new Error('Azure CLI argument contains an unsupported shell metacharacter');
  return text;
}

function terminateProcessTree(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore', windowsHide: true });
    return;
  }
  try {
    process.kill(-child.pid, 'SIGKILL');
  } catch (err) {
    if (err.code !== 'ESRCH') throw err;
  }
}

function execAz(args, timeout) {
  const isWindows = process.platform === 'win32';
  const command = isWindows ? (process.env.ComSpec || 'cmd.exe') : 'az';
  const commandArgs = isWindows
    ? ['/d', '/s', '/c', 'az.cmd', ...args.map(assertSafeCmdArg)]
    : args;
  return new Promise((resolve, reject) => {
    const child = spawn(command, commandArgs, {
      detached: !isWindows,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    });
    activeAzChild = child;
    let stderr = '';
    let timedOut = false;
    child.stderr.on('data', (chunk) => { stderr += chunk.toString(); });
    const timer = setTimeout(() => {
      timedOut = true;
      terminateProcessTree(child);
    }, timeout);
    child.once('error', (err) => {
      clearTimeout(timer);
      if (activeAzChild === child) activeAzChild = null;
      reject(err);
    });
    child.once('close', (code, signal) => {
      clearTimeout(timer);
      if (activeAzChild === child) activeAzChild = null;
      if (timedOut) {
        reject(new Error(`Azure CLI timed out after ${timeout} ms`));
      } else if (shutdownRequested && signal) {
        reject(new Error('probe interrupted'));
      } else if (code !== 0) {
        reject(new Error(`Azure CLI failed with exit code ${code}${stderr.trim() ? `: ${stderr.trim()}` : ''}`));
      } else {
        resolve();
      }
    });
  });
}

function azControl(cfg) {
  const nv = (id, value) => execAz(['apim', 'nv', 'update', '--resource-group', cfg.resourceGroup,
    '--service-name', cfg.apimName, '--named-value-id', id, '--value', String(value), '--only-show-errors', '-o', 'none'],
  cfg.controlTimeoutMs || 30000);
  let gen = Date.now() % 1000000;
  return {
    arm: async (on) => { await nv('rp-budget-demo-armed', on ? 'true' : 'false'); await sleep(cfg.propagationMs); },
    bump: async () => { gen += 1; await nv('rp-budget-demo-generation', gen); await sleep(cfg.propagationMs); },
  };
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function runEnabledWithCleanup(cfg, control) {
  cleanupControl = control;
  try {
    return await runEnabledProbe(cfg, control);
  } finally {
    await disarmDemo();
  }
}

// ---- offline self-test -------------------------------------------------------------------------------------------

function fakeGateway(flaw = '') {
  const state = { armed: false, generation: 1, counters: new Map() };
  const subs = { 'key-a': { id: 'sub-a', kind: 'store' }, 'key-b': { id: 'sub-b', kind: 'channel' } };
  const server = http.createServer((req, res) => {
    req.resume();
    req.on('end', () => {
      if (flaw === 'stall') return;
      const sub = subs[req.headers['api-key']];
      if (!sub) { res.writeHead(401).end('{}'); return; }
      const trust = flaw === 'trust-spoof';
      const kind = trust && req.headers['x-rp-budget-scope'] ? req.headers['x-rp-budget-scope'] : sub.kind;
      const demo = state.armed || (trust && req.headers['x-rp-budget-demo'] === 'true');
      const limit = demo ? 20 : 80000;
      const key = demo ? `demo-${state.generation}-${sub.id}` : sub.id;
      const used = (state.counters.get(key) || 0) + 8;
      const h = { 'x-rp-gw-budget-scope': kind, 'x-rp-gw-budget-mode': demo ? 'demo' : 'normal' };
      if (used > limit) {
        const retry = flaw === 'bad-retry' ? 'soon' : '37';
        res.writeHead(flaw === 'quota-403' ? 403 : 429, { ...h, 'retry-after': retry, 'x-rp-gw-retry-after': retry, 'x-rp-gw-quota': 'throttled',
          ...(flaw === 'missing-remaining' ? {} : { 'x-rp-gw-tokens-remaining': '0' }),
          ...(flaw === 'fabricate-consumed' ? { 'x-rp-gw-tokens-consumed': '8' } : {}) });
        res.end('{"error":"limit"}');
        return;
      }
      state.counters.set(key, used);
      res.writeHead(200, { ...h, 'x-rp-gw-quota': 'allowed', 'x-rp-gw-tokens-consumed': '8', 'x-rp-gw-tokens-remaining': String(limit - used),
        ...(flaw === 'leak-key' ? { 'x-leak': req.headers['api-key'] } : {}) });
      res.end('{}');
    });
  });
  const control = {
    arm: async (on) => { state.armed = on; },
    bump: async () => { if (flaw !== 'no-reset') state.generation += 1; },
  };
  return { server, control, state };
}

async function selfTest() {
  const scenarios = [['correct', true], ['trust-spoof', false], ['quota-403', false], ['bad-retry', false],
    ['missing-remaining', true], ['fabricate-consumed', false], ['leak-key', false], ['no-reset', false]];
  let failed = false;
  for (const [flaw, shouldPass] of scenarios) {
    const { server, control } = fakeGateway(flaw === 'correct' ? '' : flaw);
    await new Promise((r) => server.listen(0, '127.0.0.1', r));
    const cfg = { baseUrl: `http://127.0.0.1:${server.address().port}`, deployment: 'd', apiVersion: 'v',
      keyA: 'key-a', keyB: 'key-b', expectA: 'store', expectB: 'channel' };
    const results = [...await runEnabledProbe(cfg, control)];
    server.close();
    const failures = results.filter((x) => !x.ok);
    const docFailures = failures.filter((x) => x.name.startsWith('docs:'));
    const ok = failures.length === 0;
    const verdict = shouldPass ? ok : failures.length > docFailures.length;
    console.log(`${verdict ? 'ok  ' : 'FAIL'} scenario ${flaw}: ${shouldPass ? 'expected pass' : 'expected failure'} -> ${ok ? 'passed' : `failed ${failures.length} check(s)`}`);
    if (!verdict) { failed = true; failures.forEach((x) => console.log(`       FAIL [${x.name}] ${x.detail || ''}`)); }
  }
  const dis = fakeGateway('');
  await new Promise((r) => dis.server.listen(0, '127.0.0.1', r));
  const dres = await runDisabledProbe({ baseUrl: `http://127.0.0.1:${dis.server.address().port}`, deployment: 'd', apiVersion: 'v',
    keyA: 'key-a', keyB: 'key-b', expectA: 'store', expectB: 'channel' });
  dis.server.close();
  const dfail = dres.filter((x) => !x.ok);
  console.log(`${dfail.length === 0 ? 'ok  ' : 'FAIL'} scenario disabled-mode`);
  dfail.forEach((x) => console.log(`       FAIL [${x.name}] ${x.detail || ''}`));
  failed = failed || dfail.length > 0;

  const sig = fakeGateway('stall');
  await new Promise((r) => sig.server.listen(0, '127.0.0.1', r));
  const signalCfg = { baseUrl: `http://127.0.0.1:${sig.server.address().port}`, deployment: 'd', apiVersion: 'v',
    keyA: 'key-a', keyB: 'key-b', expectA: 'store', expectB: 'channel', requestTimeoutMs: 5000 };
  sig.state.armed = true;
  cleanupControl = sig.control;
  let interrupted = false;
  const stalled = post(signalCfg, signalCfg.keyA);
  setTimeout(() => requestShutdown(130), 10);
  try { await stalled; }
  catch { interrupted = shutdownRequested; }
  finally { await disarmDemo(); }
  sig.server.close();
  const signalSafe = interrupted && sig.state.armed === false;
  console.log(`${signalSafe ? 'ok  ' : 'FAIL'} scenario signal-during-armed-request-aborts-and-disarms`);
  failed = failed || !signalSafe;
  shutdownRequested = false;
  signalExitCode = null;

  const race = fakeGateway('');
  await new Promise((r) => race.server.listen(0, '127.0.0.1', r));
  const raceCfg = { baseUrl: `http://127.0.0.1:${race.server.address().port}`, deployment: 'd', apiVersion: 'v',
    keyA: 'key-a', keyB: 'key-b', expectA: 'store', expectB: 'channel' };
  const raceControl = {
    arm: async (on) => {
      race.state.armed = on;
      if (on) {
        setTimeout(() => requestShutdown(130), 10);
        await sleep(50);
      }
    },
    bump: race.control.bump,
  };
  cleanupControl = raceControl;
  interrupted = false;
  try { await runEnabledWithCleanup(raceCfg, raceControl); }
  catch { interrupted = shutdownRequested; }
  race.server.close();
  const raceSafe = interrupted && race.state.armed === false;
  console.log(`${raceSafe ? 'ok  ' : 'FAIL'} scenario signal-during-arm-unwinds-before-disarm`);
  failed = failed || !raceSafe;
  shutdownRequested = false;
  signalExitCode = null;

  let disarmAttempts = 0;
  const retryState = { armed: true };
  cleanupControl = {
    arm: async (on) => {
      disarmAttempts += 1;
      if (disarmAttempts === 1) throw new Error('synthetic disarm failure');
      retryState.armed = on;
    },
  };
  await disarmDemo();
  await disarmDemo();
  const retrySafe = disarmAttempts === 2 && retryState.armed === false && cleanupControl === null;
  console.log(`${retrySafe ? 'ok  ' : 'FAIL'} scenario failed-disarm-is-retried`);
  failed = failed || !retrySafe;

  cleanupControl = { arm: async () => { throw new Error('synthetic permanent disarm failure'); } };
  await disarmDemo();
  const failedCleanupCode = await finalizeExitCode(0);
  const failedCleanupSafe = failedCleanupCode === 1 && cleanupControl !== null;
  console.log(`${failedCleanupSafe ? 'ok  ' : 'FAIL'} scenario permanent-disarm-fails-process`);
  failed = failed || !failedCleanupSafe;
  cleanupControl = null;

  console.log(failed ? 'SELFTEST FAIL' : 'SELFTEST PASS');
  return failed ? 1 : 0;
}

async function main() {
  if (process.argv.includes('--self-test')) return selfTest();
  if (process.argv.includes('--test-az-launcher')) {
    await execAz(['version', '--output', 'none'], 30000);
    console.log('AZURE CLI LAUNCHER PASS');
    return 0;
  }
  const e = process.env;
  const cfg = {
    baseUrl: e.APIM_BASE_URL, deployment: e.DEPLOYMENT, apiVersion: e.API_VERSION,
    keyA: e.KEY_SCOPE_A, keyB: e.KEY_SCOPE_B, expectA: e.EXPECT_SCOPE_A, expectB: e.EXPECT_SCOPE_B,
    apimName: e.APIM_NAME, resourceGroup: e.RESOURCE_GROUP, propagationMs: Number(e.PROPAGATION_MS || 15000),
    requestTimeoutMs: Number(e.REQUEST_TIMEOUT_MS || 30000), controlTimeoutMs: Number(e.CONTROL_TIMEOUT_MS || 30000),
  };
  const disabled = e.TOKEN_BUDGET_DEMO === 'disabled';
  const missing = ['baseUrl', 'deployment', 'apiVersion', 'keyA', 'expectA']
    .concat(disabled ? [] : ['keyB', 'expectB', 'apimName', 'resourceGroup']).filter((k) => !cfg[k]);
  if (missing.length || !KINDS.includes(cfg.expectA)) {
    console.log(`SKIPPED: missing configuration (${missing.join(', ') || 'EXPECT_SCOPE_A must be one of ' + KINDS.join('/')})`);
    return 2;
  }
  let results;
  if (disabled) {
    results = await runDisabledProbe(cfg);
  } else {
    const control = azControl(cfg);
    results = await runEnabledWithCleanup(cfg, control);
  }
  for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'} ${r.name}${r.ok ? '' : ` — ${r.detail}`}`);
  const bad = results.filter((r) => !r.ok).length;
  console.log(bad ? `${bad} check(s) failed` : 'ALL CHECKS PASSED');
  return bad ? 1 : 0;
}

main().then(
  async (code) => {
    process.exit(await finalizeExitCode(code));
  },
  async (err) => {
    const code = await finalizeExitCode(1);
    if (!shutdownRequested) console.error(`probe error: ${err.message}`);
    process.exit(code);
  },
);
