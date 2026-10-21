import test from 'node:test';
import assert from 'node:assert/strict';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup({ configureApp, chromeOptions } = {}) {
  const app = new FakeApp();
  configureApp?.(app);
  const fake = fakeChrome(app, chromeOptions);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  const recorder = new HeaderRecorder(fake.chrome);
  controller = new Controller({ chrome: fake.chrome, link, recorder, userAgent: 'Test-UA/1', timers });
  controller.registerListeners();
  return { app, controller, link, recorder, timers, ...fake };
}

const download = (extra = {}) => ({
  id: 5,
  url: 'https://file-ap-sgp-3.gofile.io/download/web/abc/The%20Clumsy%20Witch%20Seris.zip',
  finalUrl: 'https://file-ap-sgp-3.gofile.io/download/web/abc/The%20Clumsy%20Witch%20Seris.zip',
  referrer: 'https://gofile.io/d/abc',
  filename: 'C:\\Users\\Shade\\Downloads\\The Clumsy Witch Seris.zip',
  mime: 'application/zip',
  totalBytes: 2546126931,
  state: 'in_progress',
  ...extra,
});

test('a browser download is paused, handed over with its session, then cancelled and erased', async () => {
  const t = setup();
  t.recorder.record(download().finalUrl, [
    { name: 'Cookie', value: 'accountToken=GAdjl4qbces5' },
    { name: 'Authorization', value: 'Bearer t0k' },
    { name: 'User-Agent', value: 'Browser-UA/9' },
    { name: 'Range', value: 'bytes=0-' },
  ]);

  await t.chrome.downloads.onCreated.emit(download());

  assert.deepEqual(t.calls.pause, [5]);
  assert.equal(t.app.captures.length, 1);
  const capture = t.app.captures[0];
  assert.equal(capture.url, download().finalUrl);
  assert.equal(capture.cookies, 'accountToken=GAdjl4qbces5');
  assert.equal(capture.referrer, 'https://gofile.io/d/abc');
  assert.equal(capture.userAgent, 'Browser-UA/9');
  assert.deepEqual(capture.headers, { Authorization: 'Bearer t0k' });
  assert.equal(capture.fileName, 'The Clumsy Witch Seris.zip');
  assert.equal(capture.mimeType, 'application/zip');
  assert.equal(capture.size, 2546126931);
  assert.equal(capture.source, 'download');

  assert.deepEqual(t.calls.cancel, [5]);
  assert.deepEqual(t.calls.erase, [5]);
  assert.deepEqual(t.calls.resume, [], 'a taken-over download must not be resumed');
  assert.deepEqual(t.calls.badge.slice(0, 1), ['✓']);
});

test('without recorded headers the cookies come from the cookies API (HttpOnly included)', async () => {
  const url = download().finalUrl;
  const t = setup({ chromeOptions: { cookies: { [url]: [{ name: 'accountToken', value: 'abc' }, { name: 'x', value: '1' }] } } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.equal(t.app.captures[0].cookies, 'accountToken=abc; x=1');
  assert.equal(t.app.captures[0].userAgent, 'Test-UA/1');
});

test('the original address is passed along when the browser was redirected', async () => {
  const t = setup();
  await t.chrome.downloads.onCreated.emit(download({ url: 'https://short.example/go/1', finalUrl: 'https://cdn.example/f.zip' }));
  assert.equal(t.app.captures[0].url, 'https://cdn.example/f.zip');
  assert.equal(t.app.captures[0].originalUrl, 'https://short.example/go/1');
});

test('when the app declines, the browser download is resumed untouched', async () => {
  const t = setup({ configureApp: (app) => { app.capture = () => ({ accepted: false, reason: 'excluded' }); } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.deepEqual(t.calls.pause, [5]);
  assert.deepEqual(t.calls.resume, [5]);
  assert.deepEqual(t.calls.cancel, []);
  assert.deepEqual(t.calls.erase, []);
});

test('when the app cannot be reached, the browser download is resumed', async () => {
  const t = setup({ configureApp: (app) => { app.available = false; } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.deepEqual(t.calls.resume, [5]);
  assert.deepEqual(t.calls.cancel, []);
  assert.ok(t.calls.badge.includes('!'));
  assert.match(t.link.status().lastError, /not running/);
});

test('when the app never answers a capture, the browser download is resumed after the timeout', async () => {
  const t = setup({ configureApp: (app) => { app.capture = () => null; } });
  const done = t.chrome.downloads.onCreated.emit(download());
  await new Promise((r) => setTimeout(r, 20));
  t.timers.fire((x) => x.ms === 8000);
  await done;
  assert.deepEqual(t.calls.resume, [5]);
  assert.deepEqual(t.calls.cancel, []);
});

test('if the connection drops during the hand-over, the browser download is resumed', async () => {
  const t = setup({ configureApp: (app) => { app.capture = () => null; } });
  const done = t.chrome.downloads.onCreated.emit(download());
  await new Promise((r) => setTimeout(r, 20));
  await t.app.ports[0].close();
  await done;
  assert.deepEqual(t.calls.resume, [5]);
});

test('a download that already finished is left alone', async () => {
  const t = setup({ chromeOptions: { downloads: { state: 'complete' } } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.equal(t.app.captures.length, 0);
  assert.deepEqual(t.calls.cancel, []);
  assert.deepEqual(t.calls.erase, []);
});

test('nothing is touched when the user switched capturing off in the popup', async () => {
  const t = setup({ chromeOptions: { storage: { enabled: false } } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.deepEqual(t.calls.pause, []);
  assert.equal(t.app.ports.length, 0, 'not even a connection is opened');
});

test('non-web addresses are never touched', async () => {
  const t = setup();
  await t.chrome.downloads.onCreated.emit(download({ url: 'blob:https://a/1', finalUrl: 'blob:https://a/1' }));
  await t.chrome.downloads.onCreated.emit(download({ id: 6, url: 'data:text/plain,hi', finalUrl: '' }));
  assert.deepEqual(t.calls.pause, []);
});

test('hosts the app excludes stay with the browser, even when the exclusion arrives with the hello-ack', async () => {
  const t = setup({ configureApp: (app) => { app.settings.excludedHosts = ['gofile.io']; } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.equal(t.app.captures.length, 0);
  assert.deepEqual(t.calls.pause, [5]);
  assert.deepEqual(t.calls.resume, [5], 'it was paused while asking, so it must be resumed');
  assert.deepEqual(t.calls.cancel, []);
});

test('the app switching capturing off is honoured', async () => {
  const t = setup({ configureApp: (app) => { app.settings.enabled = false; } });
  await t.chrome.downloads.onCreated.emit(download());
  assert.equal(t.app.captures.length, 0);
  assert.deepEqual(t.calls.resume, [5]);
});

test('settings pushed later apply to the next download', async () => {
  const t = setup();
  await t.chrome.downloads.onCreated.emit(download());
  await t.app.ports[0].deliver({ type: 'settings', enabled: true, excludedHosts: ['gofile.io'], minSizeBytes: 0 });
  await t.chrome.downloads.onCreated.emit(download({ id: 6 }));
  assert.equal(t.app.captures.length, 1);
  assert.deepEqual(t.calls.pause, [5]);
});

test('a download is not taken twice: two quick downloads each get their own answer', async () => {
  const t = setup();
  await Promise.all([
    t.chrome.downloads.onCreated.emit(download({ id: 1, url: 'https://a.example/1.zip', finalUrl: 'https://a.example/1.zip' })),
    t.chrome.downloads.onCreated.emit(download({ id: 2, url: 'https://a.example/2.zip', finalUrl: 'https://a.example/2.zip' })),
  ]);
  assert.deepEqual(t.app.captures.map((c) => c.url).sort(), ['https://a.example/1.zip', 'https://a.example/2.zip']);
  assert.deepEqual(t.calls.cancel.sort(), [1, 2]);
});

test('the context menu hands over a link with the page it was on', async () => {
  const t = setup();
  t.controller.createMenus();
  assert.equal(t.chrome.contextMenus.created[0].title, 'Download with ShadeNet');

  await t.chrome.contextMenus.onClicked.emit(
    { menuItemId: 'shadenet-link', linkUrl: 'https://example.com/files/a.zip', pageUrl: 'https://example.com/files' },
    { url: 'https://example.com/files' });

  const capture = t.app.captures[0];
  assert.equal(capture.url, 'https://example.com/files/a.zip');
  assert.equal(capture.referrer, 'https://example.com/files');
  assert.equal(capture.source, 'contextmenu');
  assert.deepEqual(t.calls.cancel, [], 'there is no browser download to cancel');
});

test('the context menu also takes media sources, and ignores other menus and non-web links', async () => {
  const t = setup();
  await t.chrome.contextMenus.onClicked.emit({ menuItemId: 'shadenet-link', srcUrl: 'https://cdn.example/v.mp4', pageUrl: 'https://example.com/' }, null);
  await t.chrome.contextMenus.onClicked.emit({ menuItemId: 'other', linkUrl: 'https://example.com/x.zip' }, null);
  await t.chrome.contextMenus.onClicked.emit({ menuItemId: 'shadenet-link', linkUrl: 'javascript:void(0)' }, null);
  assert.deepEqual(t.app.captures.map((c) => c.url), ['https://cdn.example/v.mp4']);
});

test('the popup can read the status, switch capturing off, and trigger a connection', async () => {
  const t = setup();
  const ask = (message) => new Promise((resolve) => t.chrome.runtime.onMessage.listeners[0](message, {}, resolve));

  let status = await ask({ type: 'status' });
  assert.equal(status.connected, false);
  assert.equal(status.localEnabled, true);

  status = await ask({ type: 'connect' });
  assert.equal(status.connected, true);
  assert.equal(status.appVersion, '9.9');

  status = await ask({ type: 'set-enabled', value: false });
  assert.equal(status.localEnabled, false);
  assert.equal(t.store.enabled, false, 'the choice is persisted');

  await t.chrome.downloads.onCreated.emit(download());
  assert.deepEqual(t.calls.pause, []);
});

// ---- the browser's "Save as" dialog -------------------------------------------------------------------------

/** Emits the file-name step the way Chrome does and resolves with the order in which things happened. */
async function determiningFilename(t, item, { settle = true } = {}) {
  const order = [];
  for (const name of ['pause', 'resume', 'cancel', 'erase']) {
    const original = t.chrome.downloads[name];
    t.chrome.downloads[name] = async (...args) => { order.push(name); return original(...args); };
  }

  let released;
  const done = new Promise((resolve) => { released = resolve; });
  const suggest = () => { order.push('suggest'); released(); };
  const keptWaiting = t.chrome.downloads.onDeterminingFilename.listeners.map((fn) => fn(item, suggest));
  assert.ok(keptWaiting.includes(true), 'the listener must say it will answer later, or the browser does not wait');
  if (settle) await done;
  return { order, done };
}

test('the file-name step is held until the app has answered, and a taken-over download is gone before it is released', async () => {
  const t = setup();
  const { order } = await determiningFilename(t, download());

  assert.deepEqual(order, ['pause', 'cancel', 'erase', 'suggest'],
    'the browser must only carry on (and maybe show its Save dialog) after the download was cancelled');
  assert.equal(t.app.captures.length, 1);
});

test('when the app declines, the browser is released only after its download was resumed', async () => {
  const t = setup({ configureApp: (app) => { app.capture = () => ({ accepted: false, reason: 'excluded' }); } });
  const { order } = await determiningFilename(t, download());
  assert.deepEqual(order, ['pause', 'resume', 'suggest']);
});

test('a download that is not ours is released at once, untouched', async () => {
  const t = setup();
  const { order } = await determiningFilename(t, download({ url: 'blob:https://example.com/x', finalUrl: 'blob:https://example.com/x' }));
  assert.deepEqual(order, ['suggest']);
  assert.equal(t.app.captures.length, 0);
});

test('when the app cannot be reached the browser still gets its download, after the wait', async () => {
  const t = setup({ configureApp: (app) => { app.available = false; } });
  const { order, done } = await determiningFilename(t, download(), { settle: false });
  t.timers.fire();   // the connect time-out passes
  await done;
  assert.deepEqual(order.slice(-2), ['resume', 'suggest']);
});

test('created and file-name events for one download are handled once', async () => {
  const t = setup();
  await Promise.all([
    t.chrome.downloads.onCreated.emit(download()),
    determiningFilename(t, download()),
  ]);
  assert.equal(t.app.captures.length, 1);
  assert.deepEqual(t.calls.cancel, [5]);
});
