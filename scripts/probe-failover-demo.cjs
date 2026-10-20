#!/usr/bin/env node
/*
 * Live probe for the authorization-gated AI Gateway failover demo (#330).
 *
 * Run AFTER deploying with apimFailoverDemoEnabled=true. It validates, against the real gateway:
 *   1. disarmed/disabled rejection   - correct key while disarmed is refused (403) and shows no demo evidence
 *   2. unauthorized rejection        - wrong or missing key while armed is refused (403)
 *   3. authorized failover           - primary really fails (4xx/5xx) and a real secondary answers 2xx,
 *                                      with evidence headers that agree with each other
 *   4. spoof resistance              - caller-supplied x-rp-gw-* evidence never appears in the response
 *   5. disarm/reset                  - after disarming, the correct key is refused again
 *
 * Usage (secrets only via environment, never logged):
 *   APIM_BASE_URL=https://<apim>.azure-api.net/inference/openai  DEPLOYMENT=<deployment> API_VERSION=<ver>
 *   APIM_SUBSCRIPTION_KEY=... FAILOVER_DEMO_KEY=...
 *   APIM_NAME=<apim> RESOURCE_GROUP=<rg>            # used to arm/disarm through `az apim nv update`
 *   [EXPECT_SECONDARY_BACKEND_ID=<id>]              # AZURE_APIM_SECONDARY_BACKEND_ID from azd outputs
 *   node scripts/probe-failover-demo.cjs
 *
 * node scripts/probe-failover-demo.cjs --self-test   runs the harness against an in-process fake gateway
 *   (no Azure) and proves it passes a correct gateway and fails broken ones. Exit 0 pass, 1 fail, 2 skipped.
 */
'use strict';
const http = require('http');
const { execFileSync } = require('child_process');

const SPOOF = {
  'x-rp-gw-failover-demo': 'active',
  'x-rp-gw-route': 'failover',
  'x-rp-gw-attempts': '2',
  'x-rp-gw-fallback': 'used',
  'x-rp-gw-primary-status': '503',
  'x-rp-gw-backend': 'spoofed-backend',
};
const EVIDENCE = ['x-rp-gw-failover-demo', 'x-rp-gw-attempts', 'x-rp-gw-fallback', 'x-rp-gw-primary-status'];

function hasDemoEvidence(h) {
  return EVIDENCE.some((n) => h.has(n)) || h.get('x-rp-gw-route') === 'failover';
}

async function post(cfg, headers) {
  const url = `${cfg.baseUrl}/deployments/${cfg.deployment}/chat/completions?api-version=${cfg.apiVersion}`;
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'api-key': cfg.subscriptionKey, 'content-type': 'application/json', ...headers },
    body: JSON.stringify({ messages: [{ role: 'user', content: 'ping' }], max_tokens: 5 }),
  });
  await res.arrayBuffer();
  return res;
}

async function runProbe(cfg, control) {
  const results = [];
  const check = (name, ok, detail) => results.push({ name, ok: !!ok, detail });

  await control.setArmed(false);
  let r = await post(cfg, { 'x-rp-failover-demo': cfg.demoKey });
  check('disarmed: correct key refused with 403', r.status === 403, `status ${r.status}`);
  check('disarmed: no failover evidence', !hasDemoEvidence(r.headers), 'evidence headers present');

  await control.setArmed(true);
  r = await post(cfg, { 'x-rp-failover-demo': `${cfg.demoKey}-wrong` });
  check('unauthorized: wrong key refused with 403', r.status === 403, `status ${r.status}`);
  check('unauthorized: no failover evidence', !hasDemoEvidence(r.headers), 'evidence headers present');
  r = await post(cfg, { 'x-rp-failover-demo': '' });
  check('unauthorized: empty key never activates the demo', !hasDemoEvidence(r.headers), 'evidence headers present');

  r = await post(cfg, { 'x-rp-failover-demo': cfg.demoKey });
  const h = r.headers;
  const primary = Number(h.get('x-rp-gw-primary-status'));
  check('authorized: final response is 2xx from the secondary', r.status >= 200 && r.status < 300, `status ${r.status}`);
  check('authorized: primary really failed', primary >= 400, `primary-status ${h.get('x-rp-gw-primary-status')}`);
  check('authorized: two attempts and fallback used', h.get('x-rp-gw-attempts') === '2' && h.get('x-rp-gw-fallback') === 'used',
    `attempts=${h.get('x-rp-gw-attempts')} fallback=${h.get('x-rp-gw-fallback')}`);
  check('authorized: route is failover', h.get('x-rp-gw-route') === 'failover', `route=${h.get('x-rp-gw-route')}`);
  check('authorized: demo marker active', h.get('x-rp-gw-failover-demo') === 'active', '');
  const backend = h.get('x-rp-gw-backend');
  check('authorized: secondary backend reported', !!backend, 'backend header missing');
  if (cfg.expectSecondary) check('authorized: backend is the expected secondary', backend === cfg.expectSecondary, `backend=${backend}`);

  const spoofHeaders = { ...SPOOF };
  r = await post(cfg, spoofHeaders);
  const sh = r.headers;
  check('spoof: injected evidence without key does not activate the demo', !hasDemoEvidence(sh), 'spoofed evidence reflected');
  check('spoof: injected backend id is not reflected', sh.get('x-rp-gw-backend') !== 'spoofed-backend', 'spoofed backend reflected');
  r = await post(cfg, { ...SPOOF, 'x-rp-failover-demo': `${cfg.demoKey}-wrong` });
  check('spoof: injected evidence with wrong key is refused', r.status === 403 && !hasDemoEvidence(r.headers), `status ${r.status}`);

  await control.setArmed(false);
  r = await post(cfg, { 'x-rp-failover-demo': cfg.demoKey });
  check('reset: disarmed again, correct key refused', r.status === 403, `status ${r.status}`);
  return results;
}

function azControl(cfg) {
  return {
    async setArmed(on) {
      execFileSync('az', ['apim', 'nv', 'update', '--service-name', cfg.apimName, '-g', cfg.resourceGroup,
        '--named-value-id', 'rp-failover-demo-armed', '--value', on ? 'true' : 'false', '--only-show-errors', '-o', 'none'],
      { stdio: ['ignore', 'ignore', 'inherit'] });
      await new Promise((r) => setTimeout(r, 5000)); // named-value propagation
    },
  };
}

// ---- offline self-test: fake gateway modelled on the policy contract ----
function startFakeGateway(mode = 'correct') {
  const state = { armed: false };
  const key = 'k'.repeat(40);
  const server = http.createServer((req, res) => {
    req.resume();
    req.on('end', () => {
      const given = req.headers['x-rp-failover-demo'] || '';
      const out = {};
      if (mode === 'spoof') for (const n of Object.keys(SPOOF)) if (req.headers[n]) out[n] = req.headers[n];
      let status = 200;
      if (given !== '') {
        if (state.armed && given === key) {
          status = mode === 'no-failover' ? 503 : 200;
          Object.assign(out, {
            'x-rp-gw-failover-demo': 'active', 'x-rp-gw-attempts': '2', 'x-rp-gw-fallback': 'used',
            'x-rp-gw-primary-status': '400', 'x-rp-gw-backend': 'secondary', 'x-rp-gw-route': 'failover',
          });
          if (mode === 'no-failover') delete out['x-rp-gw-route'];
        } else status = mode === 'open' ? 200 : 403;
      }
      res.writeHead(status, out);
      res.end('{}');
    });
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({
    server, key, state, baseUrl: `http://127.0.0.1:${server.address().port}`,
  })));
}

async function selfTest() {
  let failed = 0;
  const expectations = { correct: true, spoof: false, 'no-failover': false, open: false };
  for (const [mode, shouldPass] of Object.entries(expectations)) {
    const gw = await startFakeGateway(mode);
    const cfg = { baseUrl: gw.baseUrl, deployment: 'd', apiVersion: 'v', subscriptionKey: 's', demoKey: gw.key, expectSecondary: 'secondary' };
    const results = await runProbe(cfg, { async setArmed(on) { gw.state.armed = on; } });
    gw.server.close();
    const passed = results.every((x) => x.ok);
    const ok = passed === shouldPass;
    console.log(`${ok ? 'PASS' : 'FAIL'} self-test [${mode}]: harness ${passed ? 'accepted' : 'rejected'} the gateway (expected ${shouldPass ? 'accept' : 'reject'})`);
    if (!ok) failed++;
  }
  return failed === 0 ? 0 : 1;
}

async function main() {
  if (process.argv.includes('--self-test')) return selfTest();
  const e = process.env;
  const need = ['APIM_BASE_URL', 'DEPLOYMENT', 'API_VERSION', 'APIM_SUBSCRIPTION_KEY', 'FAILOVER_DEMO_KEY', 'APIM_NAME', 'RESOURCE_GROUP'];
  const missing = need.filter((n) => !e[n]);
  if (missing.length) { console.error(`SKIPPED: missing ${missing.join(', ')}`); return 2; }
  const cfg = {
    baseUrl: e.APIM_BASE_URL.replace(/\/+$/, ''), deployment: e.DEPLOYMENT, apiVersion: e.API_VERSION,
    subscriptionKey: e.APIM_SUBSCRIPTION_KEY, demoKey: e.FAILOVER_DEMO_KEY, apimName: e.APIM_NAME,
    resourceGroup: e.RESOURCE_GROUP, expectSecondary: e.EXPECT_SECONDARY_BACKEND_ID || '',
  };
  const control = azControl(cfg);
  let results;
  try { results = await runProbe(cfg, control); }
  finally { try { await control.setArmed(false); } catch { console.error('WARNING: could not disarm; run the reset step manually'); } }
  for (const x of results) console.log(`${x.ok ? 'PASS' : 'FAIL'} ${x.name}${x.ok ? '' : ` (${x.detail})`}`);
  return results.every((x) => x.ok) ? 0 : 1;
}

main().then((c) => process.exit(c), (err) => { console.error(`ERROR: ${err.message}`); process.exit(1); });
