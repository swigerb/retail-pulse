/**
 * Walks Demo Mode against the deployed app and captures a screenshot per act.
 *
 * The app's MSAL tokens live in sessionStorage, so a freshly launched browser always
 * starts at the sign-in gate and no amount of profile copying carries the session across.
 * The script therefore pauses for an interactive sign-in, then runs unattended.
 *
 *   node scripts/walk-demo.cjs [outputDir] [profileDir]
 *
 * Sign in when the window opens; capture begins automatically once the app is up.
 */
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const URL = 'https://calm-wave-04edb640f.7.azurestaticapps.net/';
const OUT = process.argv[2] || path.join(process.cwd(), 'demo-shots');
const PROFILE = process.argv[3] || path.join(process.env.TEMP || '/tmp', 'rp-demo-profile');
/** How long to wait for a human to complete sign-in before giving up. */
const SIGN_IN_TIMEOUT_MS = 3 * 60 * 1000;

const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  fs.mkdirSync(OUT, { recursive: true });

  const ctx = await chromium.launchPersistentContext(PROFILE, {
    headless: false,
    viewport: { width: 1600, height: 950 },
  });

  const page = ctx.pages()[0] || await ctx.newPage();
  const log = [];

  page.on('console', m => {
    if (m.type() === 'error') log.push(`CONSOLE ERROR: ${m.text().slice(0, 160)}`);
  });

  await page.goto(URL, { waitUntil: 'domcontentloaded' });
  await sleep(3000);

  if (await page.$('[data-testid="auth-signin-button"]')) {
    console.log('\n  Sign in in the browser window. Capture starts automatically.\n');
    log.push('Waited for interactive sign-in');
  }

  await page.waitForSelector('[data-testid="demo-mode-button"]', { timeout: SIGN_IN_TIMEOUT_MS });
  log.push('App loaded, starting Demo Mode');

  await page.click('[data-testid="demo-mode-button"]');
  await sleep(1500);

  const shots = [];
  let lastTitle = '';
  const deadline = Date.now() + 10 * 60 * 1000;

  while (Date.now() < deadline) {
    const card = await page.$('[data-testid="demo-mode-card"]');
    if (!card) { log.push('Demo card gone (finished or stopped)'); break; }

    const read = () => page.evaluate(() => {
      const q = s => document.querySelector(s);
      const cardEl = q('[data-testid="demo-mode-card"]');
      const drawer = q('#telemetry-drawer');
      const cr = cardEl ? cardEl.getBoundingClientRect() : null;
      const dr = drawer ? drawer.getBoundingClientRect() : null;
      return {
        title: q('[data-testid="demo-mode-title"]')?.textContent ?? '',
        progress: q('[data-testid="demo-mode-progress"]')?.textContent ?? '',
        working: q('[data-testid="demo-mode-working"]')?.textContent ?? null,
        overlapsDrawer: cr && dr ? !(cr.right <= dr.left || cr.left >= dr.right) : false,
        onScreen: cr ? cr.left >= 0 && cr.top >= 0 && cr.right <= innerWidth + 2 && cr.bottom <= innerHeight + 2 : false,
      };
    });

    let info = await read();

    if (info.title && info.title !== lastTitle) {
      lastTitle = info.title;

      // Capture the RESULT, not the act starting. The working indicator is present while
      // a prompt is in flight or an interaction is running, so wait for it to clear
      // before the screenshot; otherwise every panel is caught mid-load.
      const settleBy = Date.now() + 60_000;
      while (info.working && Date.now() < settleBy) {
        await sleep(1000);
        const nextInfo = await read();
        // The act may have advanced past this one while waiting.
        if (nextInfo.title !== info.title) { info = nextInfo; break; }
        info = nextInfo;
      }

      // A further beat so the panel paints its freshly loaded data.
      await sleep(2500);

      const n = String(shots.length + 1).padStart(2, '0');
      const safe = info.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').slice(0, 40);
      const file = path.join(OUT, `${n}-${safe}.png`);
      await page.screenshot({ path: file });

      shots.push({ n, ...info, file });
      log.push(`${info.progress} | ${info.title} | drawerOverlap=${info.overlapsDrawer} onScreen=${info.onScreen}`);
      lastTitle = info.title;
    }

    await sleep(1200);
  }

  // Count how many times each prompt was actually submitted, to prove the duplicate bug
  // is gone, and capture the market-share data shape while a session is available.
  const diagnostics = await page.evaluate(async () => {
    const text = document.body.innerText;
    const count = needle => text.split(needle).length - 1;

    let share = null;
    try {
      const rows = await (await fetch('/api/competitive/market-share')).json();
      const quarters = [...new Set(rows.map(d => d.quarter))];
      share = {
        rows: rows.length,
        quarters,
        brands: new Set(rows.map(d => d.brand)).size,
        perQuarter: Object.fromEntries(quarters.map(q => [q, rows.filter(d => d.quarter === q).length])),
      };
    } catch { /* diagnostic only */ }

    return {
      demand: count('How are FreshMart depletions trending in the Northeast this quarter?'),
      chart: count('Show a horizontal bar chart ranking all brands by depletion growth rate'),
      share,
    };
  });

  log.push(`PROMPT SUBMISSIONS: demand=${diagnostics.demand} chart=${diagnostics.chart}`);
  log.push(`MARKET SHARE: ${JSON.stringify(diagnostics.share)}`);
  log.push(`SHOTS: ${shots.length}`);

  fs.writeFileSync(path.join(OUT, 'run-log.txt'), log.join('\n'), 'utf8');
  console.log(log.join('\n'));

  await ctx.close();
})().catch(e => { console.error('FAILED:', e.message); process.exit(1); });
