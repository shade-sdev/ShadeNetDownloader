// Drives a real Chrome through the whole browser integration: loads the unpacked extension into a THROW-AWAY profile
// (never the user's), makes the browser start a download, and reports what the extension did with it.
//
//   node chrome-e2e.mjs --chrome <chrome.exe> --extension <folder> --profile <empty folder> --url <download address>
//                       [--wait-ms 25000] [--expect-id <extension id>]
//
// Chrome 137+ ignores --load-extension, so the extension is loaded the supported way: the DevTools command
// Extensions.loadUnpacked over --remote-debugging-pipe (with --enable-unsafe-extension-debugging).

import { spawn } from 'node:child_process';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['chrome', 'extension', 'profile', 'url']) {
  if (!args[required]) throw new Error(`missing --${required}`);
}
const waitMs = Number(args['wait-ms'] ?? 25000);

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
const events = [];
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
    } else {
      events.push(message);
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
  if (args['expect-id'] && loaded.id !== args['expect-id']) {
    throw new Error(`extension id ${loaded.id} differs from the expected ${args['expect-id']}`);
  }

  // the extension's service worker: our window into what the extension sees
  let worker;
  for (let i = 0; i < 40 && !worker; i++) {
    const { targetInfos } = await send('Target.getTargets');
    worker = targetInfos.find((t) => t.type === 'service_worker' && t.url.includes(loaded.id));
    if (!worker) await sleep(250);
  }
  if (!worker) throw new Error('the extension service worker did not start');
  const { sessionId } = await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });

  const evaluate = async (expression) => {
    const { result, exceptionDetails } = await send(
      'Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? 'evaluate failed');
    return result.value;
  };

  report.manifestVersion = await evaluate('chrome.runtime.getManifest().version');

  // make the browser start the download
  await send('Target.createTarget', { url: args.url });

  // the extension pauses the download, asks the app, then cancels+erases it (or resumes it): watch the list
  const seen = [];
  const deadline = Date.now() + waitMs;
  let list = [];
  while (Date.now() < deadline) {
    list = JSON.parse(await evaluate('(async () => JSON.stringify(await chrome.downloads.search({})))()'));
    for (const d of list) {
      const key = `${d.id}:${d.state}:${d.paused}`;
      if (!seen.includes(key)) seen.push(key);
    }
    if (list.length === 0 && JSON.parse(await evaluate('JSON.stringify(shadenet.controller.events)')).some((e) => e.includes('cancelled and erased'))) break;   // taken over
    if (list.some((d) => d.state === 'complete')) break;  // the browser finished it itself
    await sleep(300);
  }

  report.events = JSON.parse(await evaluate('JSON.stringify(shadenet.controller.events)'));
  report.link = JSON.parse(await evaluate('JSON.stringify(shadenet.link.status())'));
  report.statesSeen = seen;
  report.downloadsLeftInBrowser = list.map((d) => ({ id: d.id, state: d.state, paused: d.paused, url: d.url, filename: d.filename }));
  // Taken over = the extension cancelled and erased it and nothing is left in the browser. (Held at the file-name step,
  // the download can be gone before the first poll sees it, so the extension's own trace is part of the proof.)
  report.takenOver = list.length === 0 && report.events.some((e) => e.includes('cancelled and erased'));
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
