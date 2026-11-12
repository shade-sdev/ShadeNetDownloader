// A real Chrome clicks a magnet link on a page: loads the unpacked extension into a THROW-AWAY profile (never the user's), opens the
// page, clicks the link like a person would, and reports what the extension did with it (its trace).
//
//   node chrome-magnet-e2e.mjs --chrome <chrome.exe> --extension <folder> --profile <empty folder> --page <address> [--wait-ms 20000]
//
// The extension is loaded with the DevTools command Extensions.loadUnpacked over --remote-debugging-pipe (Chrome 137+ ignores
// --load-extension).

import { spawn } from 'node:child_process';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['chrome', 'extension', 'profile', 'page']) {
  if (!args[required]) throw new Error(`missing --${required}`);
}
const waitMs = Number(args['wait-ms'] ?? 20000);

const chrome = spawn(args.chrome, [
  '--remote-debugging-pipe',
  '--enable-unsafe-extension-debugging',
  `--user-data-dir=${args.profile}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-default-apps',
  '--disable-sync',
  '--disable-background-networking',
  '--window-size=900,600',
  'about:blank',
], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });

const toChrome = chrome.stdio[3];
const fromChrome = chrome.stdio[4];
let nextId = 1;
const pending = new Map();
let buffer = '';
fromChrome.on('data', (chunk) => {
  buffer += chunk.toString('utf8');
  let end;
  while ((end = buffer.indexOf('\0')) >= 0) {
    const raw = buffer.slice(0, end);
    buffer = buffer.slice(end + 1);
    if (!raw) continue;
    const message = JSON.parse(raw);
    if (message.id && pending.has(message.id)) {
      const { resolve, reject } = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) reject(new Error(`${message.error.message} (${message.error.code})`));
      else resolve(message.result ?? {});
    }
  }
});

function send(method, params = {}, sessionId) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    toChrome.write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\0');
    setTimeout(() => {
      if (pending.delete(id)) reject(new Error(`timeout waiting for ${method}`));
    }, 20000);
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = { ok: false };

async function main() {
  const loaded = await send('Extensions.loadUnpacked', { path: path.resolve(args.extension) });
  report.extensionId = loaded.id;

  let worker;
  for (let i = 0; i < 40 && !worker; i++) {
    const { targetInfos } = await send('Target.getTargets');
    worker = targetInfos.find((t) => t.type === 'service_worker' && t.url.includes(loaded.id));
    if (!worker) await sleep(250);
  }
  if (!worker) throw new Error('the extension service worker did not start');
  const { sessionId: workerSession } = await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });
  const inWorker = async (expression) => {
    const { result, exceptionDetails } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, workerSession);
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? 'evaluate failed');
    return result.value;
  };

  // the page with the magnet link, and a click on it (the content script is there once the page has loaded)
  const { targetId } = await send('Target.createTarget', { url: args.page });
  const { sessionId: pageSession } = await send('Target.attachToTarget', { targetId, flatten: true });
  const inPage = async (expression) => {
    const { result } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, pageSession);
    return result?.value;
  };
  for (let i = 0; i < 40 && (await inPage('document.readyState')) !== 'complete'; i++) await sleep(250);
  await sleep(1500);   // the content script runs at document_idle
  report.linkOnPage = await inPage('document.querySelector("a[href^=magnet]")?.href ?? null');
  await inPage('document.querySelector("a[href^=magnet]").click()');

  const deadline = Date.now() + waitMs;
  let events = [];
  while (Date.now() < deadline) {
    events = JSON.parse(await inWorker('JSON.stringify(shadenet.controller.events)'));
    if (events.some((e) => e.includes('magnet link sent'))) break;
    await sleep(300);
  }

  await sleep(2000);   // the app's answer
  report.events = JSON.parse(await inWorker('JSON.stringify(shadenet.controller.events)'));
  report.sent = report.events.some((e) => e.includes('magnet link sent'));
  report.failed = report.events.filter((e) => e.includes('magnet hand-over failed'));
  report.ok = true;
}

try {
  await main();
} catch (error) {
  report.error = String(error?.message ?? error);
}

try {
  await send('Browser.close');
} catch {
  // closing may end the pipe before the answer arrives
}
setTimeout(() => chrome.kill(), 3000).unref();
await new Promise((r) => chrome.once('exit', r));
console.log(JSON.stringify(report, null, 2));
process.exit(report.ok ? 0 : 1);
