import test from 'node:test';
import assert from 'node:assert/strict';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { cleanLinks } from '../lib/grab.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup(pageLinks, cookies = {}) {
  const app = new FakeApp();
  const fake = fakeChrome(app, { cookies });
  const scripted = [];
  fake.chrome.scripting = {
    executeScript: async (details) => {
      scripted.push(details);
      return [{ frameId: 0, result: { title: 'Holiday', links: pageLinks } }];
    },
  };
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers });
  controller.registerListeners();
  return { app, controller, scripted, ...fake };
}

test('"Download links with ShadeNet…" sends the page\'s links, with the sign-in for each of their sites', async () => {
  const t = setup(
    [
      { url: 'https://files.example/a.zip', text: 'Part A' },
      { url: 'https://files.example/b.zip' },
      { url: 'https://cdn.example/c.mp4' },
      { url: 'javascript:void(0)' },
      { url: 'https://files.example/a.zip', text: 'again' },
      { url: 'magnet:?xt=urn:btih:00&dn=x' },
    ],
    { 'https://files.example/a.zip': [{ name: 'sid', value: '42' }] });
  t.controller.createMenus();
  assert.equal(t.chrome.contextMenus.created[1].title, 'Download links with ShadeNet…');
  assert.deepEqual(t.chrome.contextMenus.created[1].contexts, ['page', 'selection', 'frame']);

  await t.chrome.contextMenus.onClicked.emit(
    { menuItemId: 'shadenet-page-links', pageUrl: 'https://site.example/holiday', frameId: 0 },
    { id: 7, url: 'https://site.example/holiday', title: 'Holiday' });

  const grab = t.app.grabs[0];
  assert.equal(grab.pageUrl, 'https://site.example/holiday');
  assert.equal(grab.title, 'Holiday');
  assert.equal(grab.userAgent, 'Test-UA/1');
  assert.deepEqual(grab.links.map((l) => l.url), [
    'https://files.example/a.zip', 'https://files.example/b.zip', 'https://cdn.example/c.mp4', 'magnet:?xt=urn:btih:00&dn=x',
  ]);
  assert.equal(grab.links[0].text, 'Part A');
  assert.deepEqual(grab.cookies, { 'files.example': 'sid=42' });
  assert.deepEqual(t.scripted[0].target, { tabId: 7, frameIds: [0] });
  assert.deepEqual(t.scripted[0].args, [false]);   // the whole page (nothing was selected)
});

test('with text selected, only the links in the selection are asked for', async () => {
  const t = setup([{ url: 'https://files.example/a.zip' }]);
  await t.chrome.contextMenus.onClicked.emit(
    { menuItemId: 'shadenet-page-links', pageUrl: 'https://site.example/', selectionText: 'a few lines', frameId: 3 },
    { id: 7, url: 'https://site.example/' });
  assert.deepEqual(t.scripted[0].args, [true]);
  assert.deepEqual(t.scripted[0].target.frameIds, [3]);
});

test('links are kept once, downloadable ones only, and at most 5000', () => {
  const many = Array.from({ length: 6000 }, (_, i) => ({ url: `https://a.example/${i}.bin` }));
  assert.equal(cleanLinks(many).length, 5000);
  assert.deepEqual(cleanLinks([{ url: ' ftp://a.example/x ' }, { url: 'data:text/plain,x' }, { url: 42 }, null]), [{ url: 'ftp://a.example/x' }]);
});
