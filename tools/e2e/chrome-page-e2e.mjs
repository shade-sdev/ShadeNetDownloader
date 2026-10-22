// Real Chrome (throw-away profile) with the extension, on a real web page: waits for the page's "Download video" button,
// takes a screenshot, clicks it, and says what the extension did. The app is started and closed by the caller.
//
//   node chrome-page-e2e.mjs --chrome <chrome.exe> --extension <folder> --profile <empty folder> --page <address> --shot <png>
//                            [--wait-ms 60000] [--expect-id <id>]

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['chrome', 'extension', 'profile', 'page', 'shot']) if (!args[required]) throw new Error(`missing --${required}`);

const chrome = spawn(args.chrome, [
  '--remote-debugging-pipe', '--enable-unsafe-extension-debugging', `--user-data-dir=${args.profile}`, '--no-first-run',
  '--no-default-browser-check', '--disable-default-apps', '--disable-sync', '--disable-background-networking',
  '--autoplay-policy=no-user-gesture-required', '--mute-audio', '--window-size=1100,800', 'about:blank',
], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });

const toChrome = chrome.stdio[3];
let nextId = 1;
const pending = new Map();
let buffer = '';
chrome.stdio[4].on('data', (chunk) => {
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
    setTimeout(() => { if (pending.delete(id)) reject(new Error(`timeout waiting for ${method}`)); }, 20000);
  });
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = { ok: false };

/** The button's own subtree (closed shadow root included): its texts and where its main part is. */
async function readPill(session) {
  const { root: document } = await send('DOM.getDocument', { depth: 0 }, session);
  const { nodeId } = await send('DOM.querySelector', { nodeId: document.nodeId, selector: 'shadenet-download' }, session);
  if (!nodeId) return { texts: [], main: null };
  const { node } = await send('DOM.describeNode', { nodeId, depth: -1, pierce: true }, session);
  const texts = [];
  let main = null;
  const walk = (n) => {
    const i = (n.attributes ?? []).indexOf('class');
    if (i >= 0 && n.attributes[i + 1] === 'main') main = n;
    if (n.nodeType === 3 && n.nodeValue.trim()) texts.push(n.nodeValue.trim());
    for (const c of n.children ?? []) walk(c);
    for (const s of n.shadowRoots ?? []) walk(s);
  };
  walk(node);
  let point = null;
  if (main) {
    try {
      const { model } = await send('DOM.getBoxModel', { backendNodeId: main.backendNodeId }, session);
      const q = model.content;
      point = { x: (q[0] + q[2]) / 2, y: (q[1] + q[5]) / 2 };
    } catch { /* not laid out yet */ }
  }
  return { texts, main: point };
}

async function main() {
  const loaded = await send('Extensions.loadUnpacked', { path: path.resolve(args.extension) });
  if (args['expect-id'] && loaded.id !== args['expect-id']) throw new Error(`extension id ${loaded.id} differs from the expected`);
  const worker = await (async () => {
    for (let i = 0; i < 40; i++) {
      const { targetInfos } = await send('Target.getTargets');
      const w = targetInfos.find((t) => t.type === 'service_worker' && t.url.includes(loaded.id));
      if (w) return w;
      await sleep(250);
    }
    throw new Error('the extension service worker did not start');
  })();
  const workerSession = (await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true })).sessionId;
  const inWorker = async (expression) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, workerSession)).result.value;

  const { targetId } = await send('Target.createTarget', { url: args.page });
  await send('Target.activateTarget', { targetId });
  const page = (await send('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  await send('DOM.enable', {}, page);
  await send('Page.enable', {}, page);

  // cookie banners and the like: the most private choice, then carry on
  const dismiss = `(() => { const b = [...document.querySelectorAll('[role="button"],button')].find((x) => /decline optional cookies|reject all|only allow essential/i.test(x.getAttribute('aria-label') || x.textContent || '')); if (b) b.click(); })()`;

  const deadline = Date.now() + Number(args['wait-ms'] ?? 60000);
  let pill;
  while (Date.now() < deadline) {
    await send('Runtime.evaluate', { expression: dismiss }, page).catch(() => {});
    pill = await readPill(page).catch(() => ({ texts: [], main: null }));
    if (pill.main && pill.texts.some((t) => /^Download video/.test(t))) break;
    await sleep(1000);
  }
  report.pill = pill;
  report.pageTitle = (await send('Target.getTargets')).targetInfos.find((t) => t.targetId === targetId)?.title;
  report.items = JSON.parse(await inWorker(`(async () => JSON.stringify((await shadenet.controller.listMedia()).items))()`));
  report.pageVideos = JSON.parse(await inWorker(`JSON.stringify([...shadenet.controller.pageVideos.values()].map((f) => [...f.values()]))`));
  await sleep(600);
  const shot = await send('Page.captureScreenshot', { format: 'png' }, page);
  fs.writeFileSync(args.shot, Buffer.from(shot.data, 'base64'));
  if (args.mode === 'page') {
    // the page route directly: what the button does for a video with no address of its own (blob:), whatever the site's player does
    const title = JSON.stringify(report.pageTitle ?? '');
    await inWorker(`shadenet.controller.sendPage({ url: ${JSON.stringify(args.page)}, title: ${title} }, { url: ${JSON.stringify(args.page)}, title: ${title} })`);
  } else {
    if (!pill.main && args.mode !== 'page') throw new Error('no Download button appeared on the page');

    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pill.main.x, y: pill.main.y }, page);
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: pill.main.x, y: pill.main.y, button: 'left', clickCount: 1 }, page);
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: pill.main.x, y: pill.main.y, button: 'left', clickCount: 1 }, page);
  }
  for (let i = 0; i < 40; i++) {
    report.events = JSON.parse(await inWorker('JSON.stringify(shadenet.controller.events)'));
    if (report.events.some((e) => /(page|video file|stream) sent/.test(e))) break;
    await sleep(300);
  }
  report.sent = report.events.find((e) => /(page|video file|stream) sent/.test(e));
  report.ok = /accepted=true/.test(report.sent ?? '');
}

try {
  await main();
} catch (error) {
  report.error = String(error?.message ?? error);
}

try { await send('Browser.close'); } catch { /* the pipe may end first */ }
setTimeout(() => chrome.kill(), 3000).unref();
await new Promise((r) => chrome.once('exit', r));
console.log(JSON.stringify(report, null, 2));
process.exit(report.ok ? 0 : 1);
