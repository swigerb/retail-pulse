import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdir, rm } from 'node:fs/promises';
import path from 'node:path';

const projectRoot = process.cwd();
const scratchRoot = path.join(projectRoot, 'node_modules', '.cache', `dashboard-layout-${process.pid}`);
const chromeBinary = process.env.CHROME_BIN || 'google-chrome';
const children = [];

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

async function waitFor(check, description, timeoutMs = 20_000) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const value = await check();
      if (value) return value;
    } catch (error) {
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
    await new Promise((resolve, reject) => {
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', reject, { once: true });
    });
    this.socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (!message.id) return;
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      if (message.error) pending.reject(new Error(message.error.message));
      else pending.resolve(message.result);
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    this.socket.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
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

  close() {
    this.socket.close();
  }
}

async function clickByText(client, selector, text) {
  const clicked = await client.evaluate(`(() => {
    const target = [...document.querySelectorAll(${JSON.stringify(selector)})]
      .find(element => element.textContent?.trim() === ${JSON.stringify(text)});
    if (!target) return false;
    target.click();
    return true;
  })()`);
  assert(clicked, `Could not find ${selector} with text "${text}"`);
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
  await clickByText(client, 'button', 'Views');
  await waitForSelector(client, '[role="menuitem"]');
  await clickByText(client, '[role="menuitem"]', 'Observability');
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
  await clickByText(client, 'button', 'Real-Time Telemetry');
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

  await clickByText(client, 'button', 'Actions');
  for (const label of ['New Chat', 'Tour', 'Demo Mode', 'Close Telemetry']) {
    assert(
      await client.evaluate(`Boolean([...document.querySelectorAll('[role="menuitem"]')]
        .find(item => item.textContent?.trim() === ${JSON.stringify(label)}))`),
      `Telemetry-open header cannot reach "${label}" at ${width}px`,
    );
  }
  await client.evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`);

  const closeClicked = await client.evaluate(`(() => {
    const button = document.querySelector('button[aria-label="Close telemetry panel"]');
    if (!button) return false;
    button.click();
    return true;
  })()`);
  assert(closeClicked, 'Telemetry drawer close button is unreachable');
  await waitFor(
    () => client.evaluate(`!document.querySelector('[data-testid="compact-actions-menu"]')`),
    'wide controls after closing telemetry',
  );
  return geometry;
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
  let viteOutput = '';
  vite.stdout.on('data', chunk => { viteOutput += chunk; });
  vite.stderr.on('data', chunk => { viteOutput += chunk; });
  await waitFor(async () => (await fetch(`http://127.0.0.1:${vitePort}`)).ok, 'Vite dev server');

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
  let chromeOutput = '';
  chrome.stderr.on('data', chunk => { chromeOutput += chunk; });

  const page = await waitFor(async () => {
    const response = await fetch(`http://127.0.0.1:${debugPort}/json`);
    if (!response.ok) return null;
    const targets = await response.json();
    return targets.find(target => target.type === 'page' && target.url.startsWith(`http://127.0.0.1:${vitePort}`));
  }, 'Chromium page');

  const client = new CdpClient(page.webSocketDebuggerUrl);
  await client.connect();
  await client.send('Page.enable');
  await client.send('Runtime.enable');
  await waitForSelector(client, '[data-testid="dashboard-shell"]');

  const results = {
    '957x1308': await verifyCompactNavigation(client, 957, 1308),
    '390x844': await verifyCompactNavigation(client, 390, 844),
    '1700x1000-telemetry': await verifyWideTelemetry(client, 1700),
    '1920x1000-telemetry': await verifyWideTelemetry(client, 1920),
  };

  await setViewport(client, 957, 1308);
  assert(
    await client.evaluate(`Boolean(document.querySelector('[data-testid="compact-actions-menu"]'))`),
    'Resize from wide desktop back to compact controls failed',
  );

  console.log(JSON.stringify(results, null, 2));
  console.log('PASS dashboard real-browser overflow regression');
  client.close();
}

try {
  await main();
} catch (error) {
  console.error(error);
  throw error;
} finally {
  for (const child of children.reverse()) {
    if (child.exitCode === null) {
      child.kill('SIGTERM');
      await new Promise(resolve => child.once('exit', resolve));
    }
  }
  await rm(scratchRoot, { recursive: true, force: true });
}
