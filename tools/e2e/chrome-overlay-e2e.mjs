// Real Chrome, real extension, real app. Two things a user asked for:
//   1. the extension finds the app by itself (the popup says "not running" while it is closed, never starts it, and shows
//      "Connected" a moment after the app starts, without anybody clicking "connect");
//   2. a video that can be downloaded is announced on the video itself, with its name; one click sends it to the app, which
//      saves it under that name.
//
//   node chrome-overlay-e2e.mjs --chrome <chrome.exe> --extension <folder> --profile <empty folder> --base <http://127.0.0.1:port>
//        --app <ShadeNetDownloader.exe> --downloads <folder> --clip <the mp4 the page plays> --shots <folder> [--expect-id <id>]
//
// The extension is loaded the supported way (Extensions.loadUnpacked over --remote-debugging-pipe) into a THROW-AWAY profile.

import { spawn, execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['chrome', 'extension', 'profile', 'base', 'app', 'downloads', 'clip', 'shots']) {
  if (!args[required]) throw new Error(`missing --${required}`);
}

const chrome = spawn(args.chrome, [
  '--remote-debugging-pipe',
  '--enable-unsafe-extension-debugging',
  `--user-data-dir=${args.profile}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-default-apps',
  '--disable-sync',
  '--disable-background-networking',
  '--autoplay-policy=no-user-gesture-required',
  '--mute-audio',
  '--window-size=1000,700',
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
const report = { ok: false, checks: {} };
const check = (name, passed, detail) => { report.checks[name] = { passed: !!passed, detail }; };
let appProcess = null;

function appPids() {
  const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq ShadeNetDownloader.exe', '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
  return out.split(/\r?\n/).filter((l) => l.includes('ShadeNetDownloader.exe')).map((l) => Number(l.split('","')[1]));
}

async function until(what, fn, ms = 20000, every = 300) {
  const deadline = Date.now() + ms;
  let last;
  while (Date.now() < deadline) {
    last = await fn();
    if (last) return last;
    await sleep(every);
  }
  throw new Error(`timed out waiting for ${what}`);
}

async function attach(targetId) {
  return (await send('Target.attachToTarget', { targetId, flatten: true })).sessionId;
}

async function shot(session, name) {
  const { data } = await send('Page.captureScreenshot', { format: 'png' }, session);
  fs.writeFileSync(path.join(args.shots, `${name}.png`), Buffer.from(data, 'base64'));
}

/** Walks the page including closed shadow roots (the DevTools can; the page itself cannot). */
async function readPill(session) {
  // only the button's own subtree is described: a whole YouTube page is far too big to pull over the pipe
  const { root: document } = await send('DOM.getDocument', { depth: 0 }, session);
  const { nodeId } = await send('DOM.querySelector', { nodeId: document.nodeId, selector: 'shadenet-download' }, session);
  const found = { texts: [], nodes: {} };
  if (!nodeId) {
    found.boxes = {};
    return found;
  }
  const { node: root } = await send('DOM.describeNode', { nodeId, depth: -1, pierce: true }, session);
  const walk = (node) => {
    if (!node) return;
    const classes = (() => { const i = (node.attributes ?? []).indexOf('class'); return i >= 0 ? node.attributes[i + 1] : ''; })();
    if (node.localName === 'shadenet-download') found.host = node;
    if (['main', 'more', 'x', 'menu'].includes(classes.split(' ')[0]) && !found.nodes[classes.split(' ')[0]]) found.nodes[classes.split(' ')[0]] = node;
    if (node.nodeType === 3 && node.nodeValue.trim()) found.texts.push(node.nodeValue.trim());
    for (const child of node.children ?? []) walk(child);
    for (const shadow of node.shadowRoots ?? []) walk(shadow);
  };
  walk(root);
  const center = async (node) => {
    if (!node) return null;
    try {
      const { model } = await send('DOM.getBoxModel', { backendNodeId: node.backendNodeId }, session);
      const q = model.content;
      return { x: (q[0] + q[2]) / 2, y: (q[1] + q[5]) / 2, width: q[2] - q[0], height: q[5] - q[1] };
    } catch {
      return null;
    }
  };
  found.boxes = { host: await center(found.host), main: await center(found.nodes.main), more: await center(found.nodes.more), menu: await center(found.nodes.menu) };
  return found;
}

async function click(session, point) {
  await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x, y: point.y }, session);
  await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: point.x, y: point.y, button: 'left', clickCount: 1 }, session);
  await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x, y: point.y, button: 'left', clickCount: 1 }, session);
}

async function main() {
  fs.mkdirSync(args.shots, { recursive: true });
  const loaded = await send('Extensions.loadUnpacked', { path: path.resolve(args.extension) });
  report.extensionId = loaded.id;
  if (args['expect-id'] && loaded.id !== args['expect-id']) throw new Error(`extension id ${loaded.id} differs from the expected ${args['expect-id']}`);

  const worker = await until('the extension service worker', async () => {
    const { targetInfos } = await send('Target.getTargets');
    return targetInfos.find((t) => t.type === 'service_worker' && t.url.includes(loaded.id));
  }, 10000, 250);
  const workerSession = await attach(worker.targetId);
  const inWorker = async (expression) => {
    const { result, exceptionDetails } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, workerSession);
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? 'evaluate failed');
    return result.value;
  };

  // ---- 1. the app is closed: the popup says so and does not start it ------------------------------------------------
  const before = appPids();
  const popup = await send('Target.createTarget', { url: `chrome-extension://${loaded.id}/popup.html` });
  await send('Target.activateTarget', { targetId: popup.targetId });
  const popupSession = await attach(popup.targetId);
  const inPopup = async (expression) => {
    const { result } = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, popupSession);
    return result.value;
  };
  const popupState = () => inPopup(`JSON.stringify({ state: document.getElementById('state').textContent, button: document.getElementById('connect').hidden ? '' : document.getElementById('connect').textContent, note: document.getElementById('note').hidden ? '' : document.getElementById('note').textContent })`)
    .then((v) => (v ? JSON.parse(v) : { state: '', button: '', note: '' }), () => ({ state: '', button: '', note: '' }));

  const closed = await until('the popup to say the app is not running', async () => {
    const s = await popupState();
    return /not running/i.test(s.state) ? s : null;
  }, 15000);
  report.popupWhileClosed = closed;
  await sleep(1500);   // a few of its two-second looks must not have started anything
  check('popup says "not running" with a Start button', /not running/i.test(closed.state) && /start/i.test(closed.button), JSON.stringify(closed));
  check('looking did not start the app', appPids().filter((p) => !before.includes(p)).length === 0, `new app processes: ${appPids().filter((p) => !before.includes(p))}`);
  await shot(popupSession, 'overlay-0-popup-app-closed');

  // ---- 2. the app starts: the popup notices by itself ---------------------------------------------------------------
  appProcess = spawn(args.app, ['--minimized'], { stdio: 'ignore' });
  const started = Date.now();
  const connected = await until('the popup to show "Connected"', async () => {
    const s = await popupState();
    return /^Connected/.test(s.state) ? s : null;
  }, 30000, 250).catch(async (error) => {
    report.diagnosis = {
      popup: await popupState(),
      link: await inWorker(`JSON.stringify({ status: shadenet.link.status(), hasPort: !!shadenet.link.port, portLaunch: shadenet.link.portLaunch, waiters: shadenet.link.helloWaiters.length })`),
      app: appPids(),
    };
    throw error;
  });
  report.popupAfterStart = { ...connected, seconds: (Date.now() - started) / 1000 };
  check('popup shows Connected by itself (no click)', /^Connected/.test(connected.state), `${connected.state} after ${report.popupAfterStart.seconds} s`);
  await shot(popupSession, 'overlay-1-popup-connected');
  await send('Target.closeTarget', { targetId: popup.targetId });

  // ---- 3. a video page: the button names the video -----------------------------------------------------------------
  const clipBytes = fs.readFileSync(args.clip);
  const page = await send('Target.createTarget', { url: `${args.base}/video.html` });
  await send('Target.activateTarget', { targetId: page.targetId });
  const pageSession = await attach(page.targetId);
  await send('DOM.enable', {}, pageSession);
  await send('Page.enable', {}, pageSession);

  const pill = await until('the Download button on the video', async () => {
    const p = await readPill(pageSession);
    return p.boxes.main && p.texts.length ? p : null;
  }, 25000, 500);
  report.pill = { texts: pill.texts, main: pill.boxes.main, host: pill.boxes.host };
  check('the button names the video', pill.texts.some((t) => t.includes('Holiday in Lisbon')), JSON.stringify(pill.texts));
  check('the button says Download', pill.texts.some((t) => /^Download video/.test(t)), '');
  const videoBox = JSON.parse((await send('Runtime.evaluate', { expression: `JSON.stringify((r => ({ x: r.x, y: r.y, w: r.width, h: r.height }))(document.querySelector('video').getBoundingClientRect()))`, returnByValue: true }, pageSession)).result.value);
  const h = pill.boxes.host;
  check('the button sits on the video', h && h.x > videoBox.x && h.x < videoBox.x + videoBox.w && h.y > videoBox.y && h.y < videoBox.y + videoBox.h, JSON.stringify({ h, videoBox }));
  await shot(pageSession, 'overlay-2-video-button');

  const tabId = await inWorker(`(async () => { const [t] = await chrome.tabs.query({ active: true, currentWindow: true }); return t?.id ?? null; })()`);
  report.badge = await inWorker(`chrome.action.getBadgeText({ tabId: ${tabId} })`);
  report.tooltip = await inWorker(`chrome.action.getTitle({ tabId: ${tabId} })`);
  check('toolbar badge counts it', report.badge === '1', report.badge);
  check('toolbar tooltip names it', /Holiday in Lisbon/.test(report.tooltip), JSON.stringify(report.tooltip));

  // ---- 4. one click: the app saves it under the video's name --------------------------------------------------------
  await click(pageSession, pill.boxes.main);
  await sleep(700);
  await shot(pageSession, 'overlay-3-sent');
  const saved = path.join(args.downloads, 'Holiday in Lisbon.mp4');
  await until('the saved file', () => (fs.existsSync(saved) && !fs.existsSync(saved + '.part') && !fs.readdirSync(args.downloads).some((f) => f.endsWith('.shadenet')) ? true : null), 30000, 500);
  await sleep(500);
  const same = crypto.createHash('sha256').update(fs.readFileSync(saved)).digest('hex') === crypto.createHash('sha256').update(clipBytes).digest('hex');
  check('the app saved "Holiday in Lisbon.mp4", byte-for-byte the video', same, `${fs.readdirSync(args.downloads).join(', ')}`);

  // ---- 5. a page with several videos: each has its own name -----------------------------------------------------------
  await send('Page.navigate', { url: `${args.base}/many.html` }, pageSession);
  const many = await until('the button on a page with three videos', async () => {
    const p = await readPill(pageSession);
    return p.boxes.more ? p : null;
  }, 25000, 500);
  report.manyTexts = many.texts;
  const items = JSON.parse(await inWorker(`(async () => JSON.stringify((await shadenet.controller.listMedia()).items.map((i) => [i.kind, i.title])))()`));
  report.manyItems = items;
  check('three videos listed, each by its own name', items.length === 3 && ['Cats on a skateboard', 'Sunrise over the bay', 'How bread is made'].every((n) => items.some((i) => i[1] === n)), JSON.stringify(items));
  check('the button on the biggest video names that video', many.texts.includes('Cats on a skateboard · 270p · 0:04') || many.texts.some((t) => t.startsWith('Cats on a skateboard ·')), '');
  await click(pageSession, many.boxes.more);
  await sleep(500);
  const opened = await readPill(pageSession);
  check('the menu lists all three', ['Cats on a skateboard', 'Sunrise over the bay', 'How bread is made'].every((n) => opened.texts.includes(n)), JSON.stringify(opened.texts));
  await shot(pageSession, 'overlay-4-several-videos');

  // ---- 5b. a video inside an iframe of ANOTHER SITE (localhost against 127.0.0.1: Chrome puts it in its own process) -------
  await send('Page.navigate', { url: `${args.base}/outer.html` }, pageSession);
  await send('Target.setDiscoverTargets', { discover: true });
  const frameTarget = await until('the iframe target', async () => {
    const { targetInfos } = await send('Target.getTargets');
    return targetInfos.find((t) => t.type === 'iframe' && t.url.includes('localhost'));
  }, 20000, 500);
  report.iframeIsSeparateProcess = !!frameTarget;
  check('the iframe is a different site, in a process of its own', !!frameTarget, frameTarget?.url);
  const frameSession = await attach(frameTarget.targetId);
  await send('DOM.enable', {}, frameSession);
  const framePill = await until('the button inside the iframe', async () => {
    const p = await readPill(frameSession);
    return p.boxes.main && p.texts.length ? p : null;
  }, 25000, 500);
  check('the iframe\'s video gets its own button, with its name', framePill.texts.some((t) => t.includes('Interview with a baker')), JSON.stringify(framePill.texts));
  const iframeBox = JSON.parse((await send('Runtime.evaluate', { expression: `JSON.stringify((r => ({ x: r.x, y: r.y }))(document.querySelector('iframe').getBoundingClientRect()))`, returnByValue: true }, pageSession)).result.value);
  await shot(pageSession, 'overlay-6-iframe');
  await click(pageSession, { x: iframeBox.x + framePill.boxes.main.x, y: iframeBox.y + framePill.boxes.main.y });
  const bakerFile = path.join(args.downloads, 'Interview with a baker.mp4');
  await until('the iframe video to be saved', () => (fs.existsSync(bakerFile) && !fs.readdirSync(args.downloads).some((f) => f.endsWith('.part')) ? true : null), 30000, 500);
  await sleep(500);
  check('one click in the iframe: saved under its name, byte for byte', crypto.createHash('sha256').update(fs.readFileSync(bakerFile)).digest('hex') === crypto.createHash('sha256').update(clipBytes).digest('hex'), '');

  // ---- 5c. a video that comes through a buffer (blob:): the page is what gets sent -------------------------------------------
  await inWorker(`shadenet.controller.extraSites.push('127.0.0.1')`);   // this test site stands in for Facebook, Instagram ...
  await send('Page.navigate', { url: `${args.base}/reel/12345.html` }, pageSession);
  const blobPill = await until('the button on a video with a blob: address', async () => {
    const p = await readPill(pageSession);
    return p.boxes.main && p.texts.some((t) => /^Download video/.test(t)) ? p : null;
  }, 25000, 500);
  const blobSrc = (await send('Runtime.evaluate', { expression: `document.querySelector('video').currentSrc.slice(0, 5)`, returnByValue: true }, pageSession)).result.value;
  check('the video really has a blob: address', blobSrc === 'blob:', blobSrc);
  check('the button offers the page (choose quality in ShadeNet)', blobPill.texts.some((t) => /choose quality/.test(t)) && blobPill.texts.some((t) => t.includes('Sunrise reel')), JSON.stringify(blobPill.texts));
  await shot(pageSession, 'overlay-7-blob');
  await click(pageSession, blobPill.boxes.main);
  const sentPage = await until('the page to be sent', async () => {
    const events = JSON.parse(await inWorker('JSON.stringify(shadenet.controller.events)'));
    return events.find((e) => /page sent/.test(e));
  }, 10000, 300);
  check('the page address went to the app and was accepted', /accepted=true/.test(sentPage), sentPage);

  // ---- 5d. a blob: player in another site's iframe, its playlist under a neutral name and type, every request refused without
  //          the PLAYER's Referer: found by what the playlist contains, read with the player's headers, downloaded by the app ------
  if (args['disguised-port']) {
    await send('Page.navigate', { url: `${args.base}/outer2.html` }, pageSession);
    const player = await until('the disguised player iframe', async () => {
      const { targetInfos } = await send('Target.getTargets');
      return targetInfos.find((t) => t.type === 'iframe' && t.url.includes(`localhost:${args['disguised-port']}/player.html`));
    }, 20000, 500);
    const playerSession = await attach(player.targetId);
    await send('DOM.enable', {}, playerSession);
    const dp = await until('the button on the disguised player', async () => {
      const p = await readPill(playerSession);
      return p.boxes.main && p.texts.some((t) => /^(Download|Try to download)/.test(t)) ? p : null;
    }, 30000, 500);
    // (a "try" may show first while the playlist is being looked at; the stream replaces it)
    const stream = await until('the stream to replace the guess', async () => {
      const p = await readPill(playerSession);
      return p.texts.some((t) => /^Download video$/.test(t)) && p.texts.some((t) => t.includes('Open film trailer')) ? p : null;
    }, 20000, 500).catch(() => null);
    check('a blob: video whose playlist has a neutral name is offered as a stream', !!stream, JSON.stringify((stream ?? dp).texts));
    const log = await (await fetch(`http://127.0.0.1:${args['disguised-port']}/__log`)).json();
    check('the extension read the playlist with the player\'s own Referer (the server refuses without it)',
      log.filter((e) => e.path === '/p/9f3a' && e.status === 200 && String(e.referer).includes('/player.html')).length >= 2 && !log.some((e) => e.path === '/p/9f3a' && e.status === 403),
      JSON.stringify(log.filter((e) => e.path === '/p/9f3a').map((e) => [e.status, e.referer])));
    const frameBox = JSON.parse((await send('Runtime.evaluate', { expression: `JSON.stringify((r => ({ x: r.x, y: r.y }))(document.querySelector('iframe').getBoundingClientRect()))`, returnByValue: true }, pageSession)).result.value);
    await shot(pageSession, 'overlay-8-disguised-player');
    const target = stream ?? dp;
    await click(pageSession, { x: frameBox.x + target.boxes.main.x, y: frameBox.y + target.boxes.main.y });
    const trailer = path.join(args.downloads, 'Open film trailer.mp4');
    await until('the stream to be saved', () => {
      if (!fs.existsSync(trailer)) return null;
      const size = fs.statSync(trailer).size;
      return sleep(1500).then(() => (fs.statSync(trailer).size === size && size > 0 ? true : null));
    }, 60000, 500).catch(async (error) => {
      report.disguisedLog = (await (await fetch(`http://127.0.0.1:${args['disguised-port']}/__log`)).json()).map((e) => [e.path, e.status, e.referer, e.ua]);
      report.events = JSON.parse(await inWorker('JSON.stringify(shadenet.controller.events)'));
      report.downloads = fs.readdirSync(args.downloads);
      throw error;
    });
    let clean = false;
    let info = '';
    try {
      const ffmpeg = args.ffmpeg;
      const errors = execFileSync(ffmpeg, ['-v', 'error', '-i', trailer, '-f', 'null', '-'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
      const probe = (() => { try { execFileSync(ffmpeg, ['-hide_banner', '-i', trailer], { encoding: 'utf8', stdio: 'pipe' }); return ''; } catch (e) { return String(e.stderr ?? ''); } })();
      clean = errors.trim() === '' && /Video:/.test(probe) && /Audio:/.test(probe) && /Duration: 00:00:0[78]/.test(probe);
      info = probe.split('\n').filter((l) => /Duration|Stream #/.test(l)).map((l) => l.trim()).join(' | ');
    } catch (error) {
      info = String(error.stderr ?? error.message).slice(0, 200);
    }
    check('the app downloaded the stream (pieces need the player\'s Referer) and the file decodes with picture and sound, 8 s', clean, info);
    const after = await (await fetch(`http://127.0.0.1:${args['disguised-port']}/__log`)).json();
    check('no piece was refused', !after.some((e) => e.status === 403 && e.path.startsWith('/s/')), JSON.stringify(after.filter((e) => e.status === 403)));
  }

  // ---- 5e. picture and sound as TWO playlists of their own: one entry in the list, one click, one file with both ----------------
  if (args.ffmpeg) {
    await send('Page.navigate', { url: `${args.base}/split.html` }, pageSession);
    const both = await until('one entry for picture + sound', async () => {
      const items = JSON.parse(await inWorker(`(async () => JSON.stringify((await shadenet.controller.listMedia()).items))()`));
      return items.length === 1 && items[0].audioUrl ? items : null;
    }, 30000, 500).catch(async () => {
      report.splitItems = JSON.parse(await inWorker(`(async () => JSON.stringify((await shadenet.controller.listMedia()).items))()`));
      report.splitTracks = JSON.parse(await inWorker(`JSON.stringify([...shadenet.controller.media.byTab.values()].flatMap((m) => [...m.values()].map((i) => [i.url, i.tracks])))`));
      return null;
    });
    check('two playlists show as ONE entry that carries the sound\'s address', !!both, JSON.stringify(both ?? report.splitItems));
    const tabIdSplit = await inWorker(`(async () => { const [t] = await chrome.tabs.query({ active: true, currentWindow: true }); return t?.id ?? null; })()`);
    check('the toolbar says 1', (await inWorker(`chrome.action.getBadgeText({ tabId: ${tabIdSplit} })`)) === '1', '');
    const splitPill = await until('the button on the split player', async () => {
      const p = await readPill(pageSession);
      return p.boxes.main && p.texts.some((t) => /picture \+ sound/.test(t)) ? p : null;
    }, 20000, 500);
    check('the button says picture + sound, with its name and size', splitPill.texts.some((t) => /Split streams film/.test(t)) && splitPill.texts.some((t) => /240p/.test(t)), JSON.stringify(splitPill.texts));
    await shot(pageSession, 'overlay-9-split-streams');
    await click(pageSession, splitPill.boxes.main);
    const splitFile = path.join(args.downloads, 'Split streams film.mp4');
    await until('the joined file', () => {
      if (!fs.existsSync(splitFile)) return null;
      const size = fs.statSync(splitFile).size;
      return sleep(1500).then(() => (fs.statSync(splitFile).size === size && size > 0 ? true : null));
    }, 60000, 500);
    let joined = false;
    let facts = '';
    try {
      const errors = execFileSync(args.ffmpeg, ['-v', 'error', '-i', splitFile, '-f', 'null', '-'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
      let probe = '';
      try { execFileSync(args.ffmpeg, ['-hide_banner', '-i', splitFile], { encoding: 'utf8', stdio: 'pipe' }); } catch (e) { probe = String(e.stderr ?? ''); }
      joined = errors.trim() === '' && /Video: h264/.test(probe) && /Audio: aac/.test(probe) && /Duration: 00:00:0[78]/.test(probe);
      facts = probe.split('\n').filter((l) => /Duration|Stream #/.test(l)).map((l) => l.trim()).join(' | ');
    } catch (error) {
      facts = String(error.stderr ?? error.message).slice(0, 200);
    }
    check('one click: ONE file with picture and sound (8 s), decoding cleanly', joined, facts);
  }

  // ---- 6. the switch in the popup ----------------------------------------------------------------------------------
  await inWorker(`(async () => { await new Promise((r) => shadenet.controller.onPopupMessage({ type: 'set-overlay', value: false }, r)); })()`);
  await sleep(1500);
  const gone = await readPill(pageSession);
  check('switched off, the button disappears from the page', !gone.host, '');
  await inWorker(`(async () => { await new Promise((r) => shadenet.controller.onPopupMessage({ type: 'set-overlay', value: true }, r)); })()`);

  // ---- 7. optional: a real YouTube watch page (nothing is downloaded) -------------------------------------------------
  if (args.youtube) {
    await send('Page.navigate', { url: args.youtube }, pageSession);
    const consent = async () => { await send('Runtime.evaluate', { expression: `(() => { const b = [...document.querySelectorAll('button')].find((x) => /reject all|accept all/i.test(x.getAttribute('aria-label') || x.textContent || '')); if (b) b.click(); })()` }, pageSession); };
    const youtube = await until('the button on a YouTube page', async () => {
      await consent();
      const p = await readPill(pageSession);
      return p.boxes.main && p.texts.some((t) => /^Download video/.test(t)) ? p : null;
    }, 40000, 1000).catch(() => null);
    report.youtube = youtube ? { texts: youtube.texts.filter((t) => t.length > 12).slice(-4) } : null;
    const pageTitle = (await send('Target.getTargets')).targetInfos.find((t) => t.targetId === page.targetId)?.title ?? '';
    const name = pageTitle.replace(/\s+-\s+YouTube$/, '');
    check('a YouTube page gets the button, named after the video', !!youtube && youtube.texts.some((t) => name && t.includes(name.slice(0, 20))), JSON.stringify({ pageTitle, texts: report.youtube?.texts }));
    await sleep(800);
    await shot(pageSession, 'overlay-5-youtube');
  }

  report.ok = Object.values(report.checks).every((c) => c.passed);
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
if (appProcess && !appProcess.killed) { try { appProcess.kill(); } catch { /* already gone */ } }
console.log(JSON.stringify(report, null, 2));
process.exit(report.ok ? 0 : 1);
