// Real Chrome, real extension: a page that requests an HLS playlist the way a player does; the extension must notice it,
// list it for the popup, badge the tab, and hand it to the app when asked.
//
//   node chrome-media-e2e.mjs --chrome <chrome.exe> --extension <folder> --profile <empty folder> --page <page address>
//                             [--wait-ms 25000] [--expect-id <extension id>]
//
// The extension is loaded the supported way (Extensions.loadUnpacked over --remote-debugging-pipe), into a THROW-AWAY profile.

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['chrome', 'extension', 'profile', 'page']) {
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
  ...(args.autoplay ? ['--autoplay-policy=no-user-gesture-required', '--mute-audio'] : []),
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
    setTimeout(() => { if (pending.delete(id)) reject(new Error(`timeout waiting for ${method}`)); }, 20000);
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = { ok: false };

async function main() {
  const loaded = await send('Extensions.loadUnpacked', { path: path.resolve(args.extension) });
  report.extensionId = loaded.id;
  if (args['expect-id'] && loaded.id !== args['expect-id']) throw new Error(`extension id ${loaded.id} differs from the expected ${args['expect-id']}`);

  let worker;
  for (let i = 0; i < 40 && !worker; i++) {
    const { targetInfos } = await send('Target.getTargets');
    worker = targetInfos.find((t) => t.type === 'service_worker' && t.url.includes(loaded.id));
    if (!worker) await sleep(250);
  }
  if (!worker) throw new Error('the extension service worker did not start');
  const { sessionId } = await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });

  const evaluate = async (expression) => {
    const { result, exceptionDetails } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? 'evaluate failed');
    return result.value;
  };

  if (args.debug) {
    await evaluate("(self.__seen = [], chrome.webRequest.onHeadersReceived.addListener((d) => self.__seen.push(d.type + ' ' + d.statusCode + ' ' + (d.url.includes('/videoplayback') ? JSON.stringify([...new URL(d.url).searchParams].map(([k, v]) => k + '=' + v.slice(0, 18))) : d.url.slice(0, 80))), { urls: ['*://*.googlevideo.com/*', '*://*.youtube.com/youtubei/*'] }), 1)");
  }

  // the page: it fetches the playlists like a player would, and becomes the active tab
  const { targetId } = await send('Target.createTarget', { url: args.page });
  await send('Target.activateTarget', { targetId });

  // a page's own player may need a nudge to start (autoplay rules, a cookie banner in front)
  const page = (await send('Target.attachToTarget', { targetId, flatten: true })).sessionId;
  const inPage = async (expression) => {
    const { result } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, page);
    return result.value;
  };
  if (args.autoplay) {
    await sleep(4000);
    report.player = await inPage(`(async () => {
      const buttons = [...document.querySelectorAll('button')].filter((b) => /reject all|accept all|no thanks|agree/i.test(b.getAttribute('aria-label') || b.textContent || ''));
      if (buttons[0]) buttons[0].click();
      const v = document.querySelector('video');
      if (v) { v.muted = true; try { await v.play(); } catch (e) { return JSON.stringify({ error: String(e) }); } }
      return JSON.stringify(v ? { paused: v.paused, t: v.currentTime, ready: v.readyState, consent: !!buttons[0] } : { video: false });
    })()`);
  }

  if (args.debug) {
    await sleep(8000);
    report.seen = JSON.parse(await evaluate('JSON.stringify(self.__seen.slice(0, 12))'));
    const shot = await send('Page.captureScreenshot', { format: 'png' }, page);
    fs.writeFileSync(args.debug, Buffer.from(shot.data, 'base64'));
  }

  // what the popup would show
  let listed = { items: [] };
  const deadline = Date.now() + waitMs;
  while (Date.now() < deadline) {
    listed = JSON.parse(await evaluate('(async () => JSON.stringify(await shadenet.controller.listMedia()))()'));
    if (listed.items.some((i) => !args.kind || i.kind === args.kind)) break;
    await sleep(400);
  }
  report.listed = listed;
  report.trace = JSON.parse(await evaluate('JSON.stringify(shadenet.controller.events)'));
  if (args.autoplay) report.playerAfter = await inPage("JSON.stringify((() => { const v = document.querySelector('video'); return v ? { paused: v.paused, t: v.currentTime, ready: v.readyState } : { video: false }; })())");
  report.tracksSeen = JSON.parse(await evaluate('JSON.stringify([...shadenet.controller.tracks.byTab.entries()].map(([t, v]) => [t, { video: v.video?.itag, audio: v.audio?.itag }]))'));

  const chosen = listed.items.find((i) => !args.kind || i.kind === args.kind);
  if (chosen) {
    report.badge = await evaluate(`(async () => await chrome.action.getBadgeText({ tabId: ${listed.tabId} }))()`);
    // "Download" in the popup
    report.sent = JSON.parse(await evaluate(`(async () => JSON.stringify(await shadenet.controller.sendMedia(${JSON.stringify(chosen.url)})))()`));
  }
  report.pageTitle = (await send('Target.getTargets')).targetInfos.find((t) => t.targetId === targetId)?.title;

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
