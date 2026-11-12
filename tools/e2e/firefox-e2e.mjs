// Drives a real Firefox through the browser integration: a THROW-AWAY profile (never the user's), the add-on loaded as a temporary
// add-on over WebDriver BiDi (Firefox's own remote protocol: no driver to install), then a page that starts a download. What
// happened to the download is checked by the caller (run-firefox-e2e.ps1): ShadeNet must have it, Firefox must not.
//
//   node firefox-e2e.mjs --firefox <firefox.exe> --extension <folder> --profile <empty folder> --downloads <folder> --url <address>
//                        [--wait-ms 25000]

import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]]] : pairs), []),
);
for (const required of ['firefox', 'extension', 'profile', 'downloads', 'url']) {
  if (!args[required]) throw new Error(`missing --${required}`);
}
const waitMs = Number(args['wait-ms'] ?? 25000);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const report = { ok: false };

// a quiet profile: no first-run pages, no updates, nothing sent anywhere, downloads into a folder of the run's own without asking
const prefs = {
  'browser.shell.checkDefaultBrowser': false,
  'browser.startup.homepage_override.mstone': 'ignore',
  'browser.aboutwelcome.enabled': false,
  'datareporting.policy.dataSubmissionEnabled': false,
  'datareporting.healthreport.uploadEnabled': false,
  'toolkit.telemetry.enabled': false,
  'app.update.disabledForTesting': true,
  'app.normandy.enabled': false,
  'browser.safebrowsing.malware.enabled': false,
  'browser.safebrowsing.phishing.enabled': false,
  'browser.safebrowsing.downloads.enabled': false,
  'network.captive-portal-service.enabled': false,
  'network.connectivity-service.enabled': false,
  'extensions.update.enabled': false,
  'browser.download.folderList': 2,
  'browser.download.dir': path.resolve(args.downloads),
  'browser.download.useDownloadDir': true,
  'browser.download.always_ask_before_handling_new_types': false,
  'browser.download.alwaysOpenPanel': false,
};
writeFileSync(path.join(args.profile, 'user.js'),
  Object.entries(prefs).map(([k, v]) => `user_pref(${JSON.stringify(k)}, ${JSON.stringify(v)});`).join('\n') + '\n');

const firefox = spawn(args.firefox, ['-profile', args.profile, '-no-remote', '-headless', '--remote-debugging-port=0', 'about:blank'],
  { stdio: ['ignore', 'pipe', 'pipe'] });

// Firefox says where BiDi listens on its error output
const address = await new Promise((resolve, reject) => {
  let text = '';
  const timer = setTimeout(() => reject(new Error('Firefox did not open WebDriver BiDi:\n' + text)), 30000);
  const look = (chunk) => {
    text += chunk.toString('utf8');
    const m = /WebDriver BiDi listening on (ws:\/\/[^\s]+)/.exec(text);
    if (m) {
      clearTimeout(timer);
      resolve(m[1]);
    }
  };
  firefox.stderr.on('data', look);
  firefox.stdout.on('data', look);
});

const socket = new WebSocket(address.endsWith('/session') ? address : address.replace(/\/?$/, '/session'));
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = (e) => reject(new Error('BiDi connection failed: ' + (e.message ?? e)));
});
let nextId = 1;
const pending = new Map();
socket.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    const { resolve, reject } = pending.get(message.id);
    pending.delete(message.id);
    if (message.type === 'error') reject(new Error(`${message.error}: ${message.message}`));
    else resolve(message.result ?? {});
  }
};
function send(method, params = {}) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
    setTimeout(() => { if (pending.delete(id)) reject(new Error(`timeout waiting for ${method}`)); }, 20000);
  });
}

try {
  await send('session.new', { capabilities: {} });
  const installed = await send('webExtension.install', { extensionData: { type: 'path', path: path.resolve(args.extension) } });
  report.extensionId = installed.extension;

  // the add-on starts and looks for the app; then the page starts the download (a sign-in cookie, then a redirect to the file)
  await sleep(2500);
  const tree = await send('browsingContext.getTree', {});
  const context = tree.contexts[0].context;
  await send('browsingContext.navigate', { context, url: args.url, wait: 'none' });
  await sleep(waitMs);
  report.ok = true;
} catch (error) {
  report.error = String(error?.message ?? error);
} finally {
  try {
    await send('browser.close', {});
  } catch {
    firefox.kill();
  }
  socket.close();
}

console.log(JSON.stringify(report));
process.exit(report.ok ? 0 : 1);
