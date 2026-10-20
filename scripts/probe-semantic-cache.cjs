#!/usr/bin/env node
/*
 * Live probe for the APIM semantic-cache policy (#339 / #342). Evidence-only: every claim comes from response headers.
 *
 * Normal mode checks, in order:
 *   1. a safe FAQ/policy request returns 200 and reports x-rp-gw-cache = miss|hit;
 *   2. an equivalent safe request (no tools) reports hit AND carries no backend contact evidence
 *      (no x-rp-gw-backend-latency-ms; x-rp-gw-tokens-consumed absent or 0);
 *   3. a caller-supplied x-rp-tenant-id header is ignored (still a hit; it is never a partition);
 *   4. bypass with a closed-vocabulary reason for dynamic, sensitive, analytics, tools, streaming and non-default
 *      temperature requests;
 *   5. no secret (subscription key) appears in any output.
 *
 * --expect-unavailable (operator has made the external cache unreachable out of band): Microsoft documents that a failed
 *   cache lookup does not raise and the call completes. The probe therefore proves only FALL-THROUGH: two equivalent safe
 *   requests both return 200, both carry backend contact evidence and neither reports hit. That is indistinguishable
 *   from a miss; the probe never claims an "unavailable" signal.
 *
 * Secrets come only from the environment and are never printed:
 *   APIM_BASE_URL=https://<apim>.azure-api.net/inference/openai  DEPLOYMENT=<deployment>  API_VERSION=<ver>
 *   SUBSCRIPTION_KEY=<APIM subscription key>
 *
 * node scripts/probe-semantic-cache.cjs [--expect-unavailable]
 * node scripts/probe-semantic-cache.cjs --self-test   in-process MODEL of the contract (no Azure); proves the harness
 *   passes a conforming gateway and fails broken ones. It is not evidence about APIM itself.
 * Exit 0 pass, 1 fail, 2 skipped (missing config).
 */
'use strict';
const http = require('http');
const https = require('https');

const SAFE_A = 'What is your return policy?';
const SAFE_B = "what's the return policy";
const REASONS = ['regulated', 'unknown-intent', 'streaming', 'tools', 'response-format', 'model-params', 'low-confidence', 'blank',
  'sensitive-intent', 'identifier', 'mutation', 'analytics', 'scoped-intent', 'dynamic-intent', 'no-partition', 'no-scope', 'unsafe', 'failed'];

function post(cfg, body, extraHeaders = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(`${cfg.baseUrl.replace(/\/$/, '')}/deployments/${encodeURIComponent(cfg.deployment)}/chat/completions?api-version=${encodeURIComponent(cfg.apiVersion)}`);
    const payload = JSON.stringify(body);
    const lib = u.protocol === 'http:' ? http : https;
    const req = lib.request(u, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'content-length': Buffer.byteLength(payload), 'api-key': cfg.key, ...extraHeaders },
      timeout: 60000,
    }, res => {
      res.on('data', () => {});
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers }));
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.end(payload);
  });
}

const chat = (user, extra = {}) => ({
  messages: [{ role: 'system', content: 'You are a retail assistant.' }, { role: 'user', content: user }],
  ...extra,
});

async function run(cfg, mode) {
  const results = [];
  const check = (name, ok, detail) => { results.push({ name, ok: !!ok }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${ok || !detail ? '' : ` — ${detail}`}`); };
  const cache = r => (r.headers['x-rp-gw-cache'] || '').toString();
  const reason = r => (r.headers['x-rp-gw-cache-reason'] || '').toString();
  const backendContact = r => r.headers['x-rp-gw-backend-latency-ms'] !== undefined;
  const tokens = r => r.headers['x-rp-gw-tokens-consumed'];
  const out = [];

  const a = await post(cfg, chat(SAFE_A));
  out.push(a);
  if (mode === 'unavailable') {
    const b = await post(cfg, chat(SAFE_B));
    check('fall-through: first safe request completes 200', a.status === 200, `status ${a.status}`);
    check('fall-through: second equivalent request completes 200', b.status === 200, `status ${b.status}`);
    check('fall-through: neither request reports hit', cache(a) !== 'hit' && cache(b) !== 'hit', `${cache(a)}/${cache(b)}`);
    check('fall-through: both reached the backend (backend latency evidence present)', backendContact(a) && backendContact(b));
    console.log('NOTE: cache unavailable is backend fall-through and is indistinguishable from a miss; no "unavailable" signal is claimed.');
  } else {
    check('safe request 200', a.status === 200, `status ${a.status}`);
    check('safe request reports miss or hit (reached lookup/store)', ['miss', 'hit'].includes(cache(a)), `x-rp-gw-cache=${cache(a) || '(absent)'} reason=${reason(a) || '-'}`);
    const b = await post(cfg, chat(SAFE_B));
    out.push(b);
    check('equivalent safe request reports hit', b.status === 200 && cache(b) === 'hit', `status ${b.status} x-rp-gw-cache=${cache(b) || '(absent)'}`);
    check('hit shows no backend contact (no backend latency)', !backendContact(b));
    check('hit consumed no backend tokens (header absent or 0)', tokens(b) === undefined || Number(tokens(b)) === 0, `tokens=${tokens(b)}`);
    const c = await post(cfg, chat(SAFE_B), { 'x-rp-tenant-id': 'attacker-tenant' });
    out.push(c);
    check('caller x-rp-tenant-id is ignored (not a partition)', c.status === 200 && cache(c) === 'hit', `x-rp-gw-cache=${cache(c) || '(absent)'}`);

    const bypass = [
      ['dynamic', chat('What is the price of a gift card today?'), 'dynamic-intent'],
      ['sensitive', chat('What is the pharmacy recall refund policy?'), 'sensitive-intent'],
      ['analytics', chat('What is the return rate by store this month?'), null],
      ['tools', chat(SAFE_A, { tools: [{ type: 'function', function: { name: 'x', parameters: { type: 'object', properties: {} } } }] }), 'tools'],
      ['streaming', chat(SAFE_A, { stream: true }), 'streaming'],
      ['temperature', chat(SAFE_A, { temperature: 0.2 }), 'model-params'],
    ];
    for (const [name, body, want] of bypass) {
      const r = await post(cfg, body);
      out.push(r);
      const rs = reason(r);
      check(`${name} request bypasses the cache`, cache(r) === 'bypass' && REASONS.includes(rs) && (want === null || rs === want), `x-rp-gw-cache=${cache(r) || '(absent)'} reason=${rs || '-'}`);
    }
  }

  const leaked = JSON.stringify(out).includes(cfg.key);
  check('no secret in captured evidence', !leaked);
  return results.every(r => r.ok);
}

// ---- self-test: an in-process MODEL of the documented contract (not APIM) ------------------------------------------
function fakeGateway(opts = {}) {
  const store = new Set();
  return http.createServer((req, res) => {
    let raw = '';
    req.on('data', d => { raw += d; });
    req.on('end', () => {
      const body = JSON.parse(raw || '{}');
      const h = { 'content-type': 'application/json' };
      const user = (body.messages || []).filter(m => m.role === 'user').map(m => m.content).pop() || '';
      let decision = 'eligible';
      if (body.tools) decision = 'tools';
      else if (body.stream) decision = 'streaming';
      else if (body.temperature !== undefined && body.temperature !== 1) decision = 'model-params';
      else if (/pharmac|recall/i.test(user)) decision = 'sensitive-intent';
      else if (/price|today/i.test(user)) decision = 'dynamic-intent';
      else if (/rate|month|store/i.test(user)) decision = 'analytics';
      if (decision !== 'eligible') { h['x-rp-gw-cache'] = 'bypass'; h['x-rp-gw-cache-reason'] = decision; h['x-rp-gw-backend-latency-ms'] = '5'; }
      else {
        const key = (opts.useHeaderPartition ? req.headers['x-rp-tenant-id'] || '' : '') + '|return-policy';
        const hit = !opts.neverHit && opts.cacheUp !== false && store.has(key);
        if (hit) { h['x-rp-gw-cache'] = 'hit'; if (opts.hitLeaksBackend) h['x-rp-gw-backend-latency-ms'] = '5'; }
        else { if (opts.cacheUp !== false) store.add(key); h['x-rp-gw-cache'] = 'miss'; h['x-rp-gw-backend-latency-ms'] = '5'; h['x-rp-gw-tokens-consumed'] = '120'; }
      }
      if (opts.leak) h['x-debug'] = req.headers['api-key'];
      res.writeHead(200, h);
      res.end('{}');
    });
  });
}

async function selfTest() {
  const key = 'selftest-key-0123456789abcdef';
  const cases = [
    ['conforming gateway passes', {}, 'normal', true],
    ['cache-unavailable fall-through passes', { cacheUp: false }, 'unavailable', true],
    ['never-hit gateway fails', { neverHit: true }, 'normal', false],
    ['hit that contacts backend fails', { hitLeaksBackend: true }, 'normal', false],
    ['caller-header partition fails', { useHeaderPartition: true }, 'normal', false],
    ['secret leak fails', { leak: true }, 'normal', false],
    ['unavailable mode fails if a hit appears', {}, 'unavailable', false],
  ];
  const realLog = console.log;
  let ok = true;
  for (const [name, opts, mode, expected] of cases) {
    const server = fakeGateway(opts);
    await new Promise(r => server.listen(0, '127.0.0.1', r));
    const cfg = { baseUrl: `http://127.0.0.1:${server.address().port}/openai`, deployment: 'd', apiVersion: 'v', key };
    console.log = () => {};
    let got;
    try { got = await run(cfg, mode); } finally { console.log = realLog; }
    server.close();
    const good = got === expected;
    ok = ok && good;
    console.log(`${good ? 'PASS' : 'FAIL'} self-test: ${name}`);
  }
  return ok;
}

(async () => {
  const args = process.argv.slice(2);
  if (args.includes('--self-test')) process.exit((await selfTest()) ? 0 : 1);
  const cfg = { baseUrl: process.env.APIM_BASE_URL, deployment: process.env.DEPLOYMENT, apiVersion: process.env.API_VERSION, key: process.env.SUBSCRIPTION_KEY };
  if (!cfg.baseUrl || !cfg.deployment || !cfg.apiVersion || !cfg.key) {
    console.log('SKIPPED: set APIM_BASE_URL, DEPLOYMENT, API_VERSION and SUBSCRIPTION_KEY (values are never printed).');
    process.exit(2);
  }
  try {
    process.exit((await run(cfg, args.includes('--expect-unavailable') ? 'unavailable' : 'normal')) ? 0 : 1);
  } catch (e) {
    console.log(`FAIL probe error: ${e.code || e.message}`);
    process.exit(1);
  }
})();
