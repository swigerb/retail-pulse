import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';

const projectRoot = process.cwd();
const scratchRoot = path.join(projectRoot, 'node_modules', '.cache', `dashboard-layout-${process.pid}`);
const chromeBinary = process.env.CHROME_BIN || 'google-chrome';
const children = [];
const CHECK_TIMEOUT_MS = 3_000;
const CLEANUP_TIMEOUT_MS = 3_000;
let cdpClient;

class FatalHarnessError extends Error {}

function assert(condition, message, details) {
  if (!condition) {
    throw new Error(`${message}${details ? `\n${JSON.stringify(details, null, 2)}` : ''}`);
  }
}

async function allocatePort() {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => resolve(address.port));
    });
  });
}

async function withTimeout(promise, timeoutMs, description) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error(`Timed out after ${timeoutMs}ms: ${description}`)),
          timeoutMs,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

async function waitFor(check, description, timeoutMs = 20_000) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const value = await withTimeout(
        Promise.resolve().then(check),
        Math.min(CHECK_TIMEOUT_MS, Math.max(1, deadline - Date.now())),
        `${description} check`,
      );
      if (value) return value;
    } catch (error) {
      if (error instanceof FatalHarnessError) throw error;
      lastError = error;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out waiting for ${description}${lastError ? `: ${lastError}` : ''}`);
}

class CdpClient {
  constructor(url) {
    this.nextId = 1;
    this.pending = new Map();
    this.socket = new WebSocket(url);
  }

  async connect() {
    await withTimeout(new Promise((resolve, reject) => {
      const fail = event => reject(new Error(`CDP WebSocket failed to connect: ${event.type}`));
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', fail, { once: true });
      this.socket.addEventListener('close', fail, { once: true });
    }), CHECK_TIMEOUT_MS, 'CDP WebSocket connection');
    this.socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (!message.id) return;
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      if (message.error) pending.reject(new Error(message.error.message));
      else pending.resolve(message.result);
    });
    const rejectPending = event => {
      const error = new Error(`CDP WebSocket ${event.type} with ${this.pending.size} operation(s) pending`);
      for (const pending of this.pending.values()) pending.reject(error);
      this.pending.clear();
    };
    this.socket.addEventListener('error', rejectPending);
    this.socket.addEventListener('close', rejectPending);
  }

  send(method, params = {}) {
    const id = this.nextId++;
    this.socket.send(JSON.stringify({ id, method, params }));
    return withTimeout(
      new Promise((resolve, reject) => this.pending.set(id, { resolve, reject })),
      CHECK_TIMEOUT_MS,
      `CDP ${method}`,
    ).finally(() => this.pending.delete(id));
  }

  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
    });
    if (result.exceptionDetails) {
      throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    }
    return result.result.value;
  }

  async close() {
    if (this.socket.readyState === WebSocket.CLOSED) return;
    const closed = new Promise(resolve => this.socket.addEventListener('close', resolve, { once: true }));
    this.socket.close();
    await withTimeout(closed, CLEANUP_TIMEOUT_MS, 'CDP WebSocket close');
  }
}

async function activateByText(client, selector, text) {
  const target = await client.evaluate(`(() => {
    const target = [...document.querySelectorAll(${JSON.stringify(selector)})]
      .find(element => element.textContent?.trim() === ${JSON.stringify(text)});
    if (!target) return null;
    target.focus();
    if (document.activeElement !== target) return null;
    const bounds = target.getBoundingClientRect();
    return { x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 };
  })()`);
  assert(target, `Could not focus ${selector} with text "${text}"`);
  await client.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: target.x, y: target.y, button: 'left', clickCount: 1 });
  await client.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: target.x, y: target.y, button: 'left', clickCount: 1 });
}

async function activateSelector(client, selector) {
  const target = await client.evaluate(`(() => {
    const target = document.querySelector(${JSON.stringify(selector)});
    if (!target) return null;
    target.focus();
    if (document.activeElement !== target) return null;
    const bounds = target.getBoundingClientRect();
    return { x: bounds.left + bounds.width / 2, y: bounds.top + bounds.height / 2 };
  })()`);
  assert(target, `Could not focus ${selector}`);
  await client.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: target.x, y: target.y, button: 'left', clickCount: 1 });
  await client.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: target.x, y: target.y, button: 'left', clickCount: 1 });
}

async function waitForSelector(client, selector) {
  return waitFor(
    () => client.evaluate(`Boolean(document.querySelector(${JSON.stringify(selector)}))`),
    selector,
  );
}

async function setViewport(client, width, height) {
  await client.send('Emulation.setDeviceMetricsOverride', {
    width,
    height,
    deviceScaleFactor: 1,
    mobile: false,
  });
  await client.evaluate('window.dispatchEvent(new Event("resize"))');
  await new Promise(resolve => setTimeout(resolve, 350));
}

async function resetToChat(client) {
  await client.send('Page.reload', { ignoreCache: true });
  await new Promise(resolve => setTimeout(resolve, 500));
  await waitForSelector(client, '[data-testid="dashboard-shell"]');
}

async function selectObservability(client) {
  await activateByText(client, 'button', 'Views');
  await waitForSelector(client, '[role="menuitem"]');
  await activateByText(client, '[role="menuitem"]', 'Observability');
  await waitForSelector(client, '[data-testid="observability-panel"]');
}

async function collectGeometry(client) {
  return client.evaluate(`(() => {
    const rect = selector => {
      const element = document.querySelector(selector);
      if (!element) return null;
      const bounds = element.getBoundingClientRect();
      return {
        left: bounds.left,
        right: bounds.right,
        top: bounds.top,
        width: bounds.width,
        scrollWidth: element.scrollWidth,
        clientWidth: element.clientWidth,
        scrollLeft: element.scrollLeft,
      };
    };
    return {
      viewport: { width: innerWidth, height: innerHeight },
      root: rect('html'),
      body: rect('body'),
      shell: rect('[data-testid="dashboard-shell"]'),
      header: rect('[data-testid="dashboard-header"]'),
      headerActions: rect('[data-testid="dashboard-header-actions"]'),
      main: rect('[data-testid="dashboard-main"]'),
      heading: rect('[data-testid="observability-panel"] h1'),
      drawer: rect('#telemetry-drawer'),
    };
  })()`);
}

function assertNoHorizontalDisplacement(geometry) {
  const width = geometry.viewport.width;
  assert(geometry.root.scrollWidth === width, 'Document root horizontally overflows', geometry);
  assert(geometry.shell.scrollWidth === width, 'Dashboard shell horizontally overflows', geometry);
  assert(geometry.shell.scrollLeft === 0, 'Dashboard shell was horizontally scrolled', geometry);
  assert(Math.abs(geometry.header.left) < 0.5, 'Header was horizontally displaced', geometry);
  assert(Math.abs(geometry.main.left) < 0.5, 'Main content was horizontally displaced', geometry);
  assert(geometry.header.right <= width + 0.5, 'Header extends beyond the viewport', geometry);
}

async function verifyCompactNavigation(client, width, height) {
  await setViewport(client, width, height);
  await resetToChat(client);
  await selectObservability(client);
  const geometry = await collectGeometry(client);
  assertNoHorizontalDisplacement(geometry);
  assert(geometry.heading, 'Observability heading is missing', geometry);
  assert(geometry.heading.left >= 0 && geometry.heading.left < width, 'Heading is offscreen', geometry);
  assert(geometry.heading.right > 0, 'Heading is clipped outside the viewport', geometry);
  return geometry;
}

async function openTelemetry(client) {
  await activateByText(client, 'button', 'Real-Time Telemetry');
  await waitForSelector(client, '#telemetry-drawer');
  await waitFor(
    () => client.evaluate(`(() => {
      const drawer = document.querySelector('#telemetry-drawer')?.getBoundingClientRect();
      const actions = document.querySelector('[data-testid="dashboard-header-actions"]')?.getBoundingClientRect();
      return Boolean(drawer && actions && drawer.width > 0
        && drawer.right <= innerWidth + 1 && actions.right <= drawer.left + 1);
    })()`),
    'open telemetry drawer',
  );
  await waitForSelector(client, '[data-testid="compact-actions-menu"]');
}

async function verifyWideTelemetry(client, width) {
  await setViewport(client, width, 1000);
  await resetToChat(client);
  assert(
    await client.evaluate(`Boolean([...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'New Chat'))`),
    `Wide ${width}px header did not use standard controls before opening telemetry`,
  );

  await openTelemetry(client);
  const geometry = await collectGeometry(client);
  assertNoHorizontalDisplacement(geometry);
  assert(geometry.drawer, 'Telemetry drawer is missing', geometry);
  assert(
    geometry.headerActions.right <= geometry.drawer.left + 0.5,
    'Essential header controls are hidden behind the telemetry drawer',
    geometry,
  );

  await activateByText(client, 'button', 'Actions');
  for (const label of ['New Chat', 'Tour', 'Demo Mode', 'Close Telemetry']) {
    assert(
      await client.evaluate(`Boolean([...document.querySelectorAll('[role="menuitem"]')]
        .find(item => item.textContent?.trim() === ${JSON.stringify(label)}))`),
      `Telemetry-open header cannot reach "${label}" at ${width}px`,
    );
  }
  await client.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);

  await activateSelector(client, 'button[aria-label="Close telemetry panel"]');
  await waitFor(
    () => client.evaluate(`!document.querySelector('[data-testid="compact-actions-menu"]')`),
    'wide controls after closing telemetry',
  );
  return geometry;
}

async function verifyPackColors(client) {
  await setViewport(client, 1920, 1000);
  const results = {};
  let baseline;
  for (const key of ['default', 'walgreens-retail', 'kroger-retail', 'staples-retail', 'lowes-retail']) {
    const manifest = await readFile(path.join(projectRoot, '../../packs', key, 'pack.yaml'), 'utf8');
    const theme = Object.fromEntries(['primaryColor', 'accentColor', 'fontFamily'].map(field => {
      const match = manifest.match(new RegExp(`^    ${field}: "([^"]+)"`, 'm'));
      assert(match, `${key} is missing ${field}`);
      return [field, match[1]];
    }));
    // Exercise useActivePack -> Dashboard.applyPackTheme, not a manual CSS override.
    const { identifier } = await client.send('Page.addScriptToEvaluateOnNewDocument', {
      source: `(() => {
        const originalFetch = window.fetch.bind(window);
        window.fetch = (input, options) => {
          const url = new URL(typeof input === 'string' ? input : input.url, location.href);
          if (url.pathname === '/api/pack') return Promise.resolve(Response.json({
            key: ${JSON.stringify(key)}, displayName: ${JSON.stringify(key)},
            tenant: { company: ${JSON.stringify(key)}, theme: ${JSON.stringify(theme)} }
          }));
          if (url.pathname === '/api/pack/starting-tasks') return Promise.resolve(Response.json({
            packKey: ${JSON.stringify(key)}, categories: []
          }));
          if (url.pathname.includes('/chat')) throw new Error('Color checks must never submit a model prompt');
          return originalFetch(input, options);
        };
      })();`,
    });
    try {
      await resetToChat(client);
      await waitFor(() => client.evaluate(
        `document.documentElement.style.getPropertyValue('--brand-accent') === ${JSON.stringify(theme.accentColor)}`,
      ), `${key} pack theme`);

      const readColors = () => client.evaluate(`(() => {
        const send = document.querySelector('[data-testid="chat-send-button"]');
        const style = getComputedStyle(send);
        return {
          logos: [...document.querySelectorAll('.brand-logo > div:first-child')]
            .map(logo => getComputedStyle(logo).backgroundImage),
          gradient: style.backgroundImage, color: style.color,
          disabled: send.disabled, focused: document.activeElement === send,
          focusVisible: send.hasAttribute('data-fui-focus-visible'),
          focusShadow: style.boxShadow,
          headerBorder: getComputedStyle(document.querySelector('header')).borderBottomColor,
          accentSoft: getComputedStyle(document.documentElement).getPropertyValue('--brand-accent-soft').trim(),
        };
      })()`);
      const disabled = await readColors();
      assert(disabled.disabled, `${key}: empty Send must remain disabled`);

      await client.evaluate(`document.querySelector('#chat-input').focus()`);
      await client.send('Input.insertText', { text: 'Unsent color regression check' });
      await waitFor(() => client.evaluate(
        `!document.querySelector('[data-testid="chat-send-button"]').disabled`,
      ), 'enabled Send');
      const enabled = await readColors();
      const bounds = await client.evaluate(`(() => {
        const r = document.querySelector('[data-testid="chat-send-button"]').getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
      })()`);
      await client.send('Input.dispatchMouseEvent', { type: 'mouseMoved', ...bounds });
      const hover = await readColors();
      await client.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 0, y: 0 });
      await client.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
      await client.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
      const focus = await readColors();
      assert(focus.focused, `${key}: keyboard Tab must reach enabled Send`, focus);
      assert(focus.focusVisible && focus.focusShadow.includes('inset'),
        `${key}: Fluent keyboard focus ring must remain visible`, focus);

      if (process.env.BRAND_SCREENSHOT_DIR && ['default', 'walgreens-retail'].includes(key)) {
        await mkdir(process.env.BRAND_SCREENSHOT_DIR, { recursive: true });
        const image = await client.send('Page.captureScreenshot', { format: 'png' });
        await writeFile(path.join(process.env.BRAND_SCREENSHOT_DIR, `${key}.png`), Buffer.from(image.data, 'base64'));
      }

      for (const [state, colors] of Object.entries({ disabled, enabled, hover, focus })) {
        assert(colors.logos.length >= 1 && colors.logos.every(gradient =>
          gradient === 'linear-gradient(135deg, rgb(21, 101, 192), rgb(66, 165, 245))'),
        `${key}/${state}: RP must retain Retail Pulse blue`, colors);
        assert(colors.gradient === 'linear-gradient(135deg, rgb(21, 101, 192) 0%, rgb(66, 165, 245) 100%)',
          `${key}/${state}: Send must retain Retail Pulse blue`, colors);
        assert(colors.color === 'rgb(255, 255, 255)', `${key}/${state}: Send text color changed`, colors);
        if (baseline) {
          assert(colors.focusVisible === baseline[state].focusVisible && colors.focusShadow === baseline[state].focusShadow,
            `${key}/${state}: Send focus styling changed across packs`, colors);
        }
      }
      const expectedAccent = await client.evaluate(`(() => {
        const probe = document.createElement('div');
        probe.style.color = ${JSON.stringify(theme.accentColor)};
        document.body.append(probe);
        const color = getComputedStyle(probe).color;
        probe.remove();
        return color;
      })()`);
      assert(enabled.headerBorder === expectedAccent, `${key}: retailer header accent must still apply`, enabled);
      if (key !== 'default') {
        assert(enabled.accentSoft !== baseline.enabled.accentSoft,
          `${key}: derived retailer accents must still change`, enabled);
      }
      const states = { disabled, enabled, hover, focus };
      baseline ??= states;
      results[key] = { theme, ...states };
    } finally {
      await client.send('Page.removeScriptToEvaluateOnNewDocument', { identifier });
    }
  }
  return results;
}

async function main() {
  const vitePort = await allocatePort();
  const debugPort = await allocatePort();
  await mkdir(scratchRoot, { recursive: true });

  const vite = spawn(
    process.execPath,
    [path.join(projectRoot, 'node_modules', 'vite', 'bin', 'vite.js'), '--host', '127.0.0.1', '--port', String(vitePort), '--strictPort'],
    { cwd: projectRoot, stdio: ['ignore', 'pipe', 'pipe'] },
  );
  children.push(vite);
  vite.spawnError = null;
  vite.on('error', error => { vite.spawnError = error; });
  let viteOutput = '';
  vite.stdout.on('data', chunk => { viteOutput += chunk; });
  vite.stderr.on('data', chunk => { viteOutput += chunk; });
  await waitFor(async () => {
    if (vite.spawnError) throw new FatalHarnessError(`Vite failed to spawn: ${vite.spawnError.message}\n${viteOutput}`);
    if (vite.exitCode !== null) throw new FatalHarnessError(`Vite exited with code ${vite.exitCode}\n${viteOutput}`);
    return (await fetch(`http://127.0.0.1:${vitePort}`, { signal: AbortSignal.timeout(CHECK_TIMEOUT_MS) })).ok;
  }, 'Vite dev server');

  const chrome = spawn(chromeBinary, [
    '--headless=new',
    '--no-sandbox',
    '--disable-gpu',
    '--disable-dev-shm-usage',
    `--remote-debugging-port=${debugPort}`,
    `--user-data-dir=${scratchRoot}`,
    `http://127.0.0.1:${vitePort}`,
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  children.push(chrome);
  chrome.spawnError = null;
  chrome.on('error', error => { chrome.spawnError = error; });
  let chromeOutput = '';
  chrome.stderr.on('data', chunk => { chromeOutput += chunk; });

  const page = await waitFor(async () => {
    if (chrome.spawnError) throw new FatalHarnessError(`Chrome failed to spawn: ${chrome.spawnError.message}\n${chromeOutput}`);
    if (chrome.exitCode !== null) throw new FatalHarnessError(`Chrome exited with code ${chrome.exitCode}\n${chromeOutput}`);
    const response = await fetch(`http://127.0.0.1:${debugPort}/json`, {
      signal: AbortSignal.timeout(CHECK_TIMEOUT_MS),
    });
    if (!response.ok) return null;
    const targets = await response.json();
    return targets.find(target => target.type === 'page' && target.url.startsWith(`http://127.0.0.1:${vitePort}`));
  }, 'Chromium page');

  const client = new CdpClient(page.webSocketDebuggerUrl);
  cdpClient = client;
  await client.connect();
  await client.send('Page.enable');
  await client.send('Runtime.enable');
  await waitForSelector(client, '[data-testid="dashboard-shell"]');

  const results = {
    '957x1308': await verifyCompactNavigation(client, 957, 1308),
    '390x844': await verifyCompactNavigation(client, 390, 844),
    '1700x1000-telemetry': await verifyWideTelemetry(client, 1700),
    '1920x1000-telemetry': await verifyWideTelemetry(client, 1920),
    'pack-colors': await verifyPackColors(client),
  };

  await setViewport(client, 957, 1308);
  assert(
    await client.evaluate(`Boolean(document.querySelector('[data-testid="compact-actions-menu"]'))`),
    'Resize from wide desktop back to compact controls failed',
  );

  console.log(JSON.stringify(results, null, 2));
  console.log('PASS dashboard real-browser overflow and pack-color regressions');
}

async function stopChild(child) {
  if (child.exitCode !== null || child.signalCode !== null || child.spawnError) return;
  const exited = new Promise(resolve => child.once('exit', resolve));
  child.kill('SIGTERM');
  try {
    await withTimeout(exited, CLEANUP_TIMEOUT_MS, `${child.spawnfile || 'child process'} SIGTERM`);
  } catch {
    if (child.pid) child.kill('SIGKILL');
    await withTimeout(exited, CLEANUP_TIMEOUT_MS, `${child.spawnfile || 'child process'} SIGKILL`);
  }
}

try {
  await main();
} catch (error) {
  console.error(error);
  throw error;
} finally {
  const cleanupErrors = [];
  if (cdpClient) {
    try {
      await cdpClient.close();
    } catch (error) {
      cleanupErrors.push(error);
    }
  }
  const childResults = await Promise.allSettled(children.reverse().map(stopChild));
  cleanupErrors.push(...childResults.filter(result => result.status === 'rejected').map(result => result.reason));
  try {
    await withTimeout(
      rm(scratchRoot, { recursive: true, force: true }),
      CLEANUP_TIMEOUT_MS,
      'Chrome profile cleanup',
    );
  } catch (error) {
    cleanupErrors.push(error);
  }
  for (const error of cleanupErrors) console.error(`Cleanup failure: ${error.message}`);
}
