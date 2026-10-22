// Playlists a player serves under neutral names and types, and the extension's own look at them with the player's headers.
import test from 'node:test';
import assert from 'node:assert/strict';
import { MediaTracker, worthALook } from '../lib/media.js';
import { Controller, defaultFetchText } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

const response = (url, type, extra = {}) => ({
  tabId: 7, url, type: 'xmlhttprequest', statusCode: 200,
  responseHeaders: [{ name: 'Content-Type', value: type }, ...(extra.length != null ? [{ name: 'Content-Length', value: String(extra.length) }] : [])],
  documentUrl: 'https://player.example/embed/42', ...extra.details,
});
const PLAYLIST = '#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI="i.jpg"\n#EXTINF:2.0,\ns0.jpg\n#EXTINF:2.0,\ns1.jpg\n#EXT-X-ENDLIST\n';

test('which answers are worth reading: small, neutral, from a script, not a known piece or script', () => {
  const ok = (url, type, extra) => worthALook(response(url, type, extra), type);
  assert.equal(ok('https://p.example/p/9f3a', 'application/octet-stream', { length: 900 }), true);
  assert.equal(ok('https://p.example/list', 'text/plain; charset=utf-8'), true);
  assert.equal(ok('https://p.example/x', '', { length: 400 }), true);
  assert.equal(ok('https://p.example/poster.jpg', 'image/jpeg', { length: 2000 }), true);          // a playlist can hide as a picture

  assert.equal(ok('https://p.example/api', 'application/json'), false);
  assert.equal(ok('https://p.example/p/1', 'application/octet-stream', { length: 5_000_000 }), false);   // a video piece
  assert.equal(ok('https://p.example/seg.jpg', 'image/jpeg'), false);                                   // an "image" of unknown size
  assert.equal(ok('https://p.example/app.js', 'text/plain'), false);
  assert.equal(ok('https://p.example/seg/1.ts', 'application/octet-stream'), false);
  assert.equal(worthALook({ ...response('https://p.example/p/1', 'text/plain'), type: 'image' }, 'text/plain'), false);
  assert.equal(worthALook({ ...response('https://p.example/p/1', 'text/plain'), statusCode: 403 }, 'text/plain'), false);
});

test('a playlist with a neutral name is found by what it contains, and remembers the player\'s page', async () => {
  const read = [];
  const tracker = new MediaTracker({ fetchText: async (url) => { read.push(url); return PLAYLIST; } });
  await tracker.consider(response('https://p.example/p/9f3a', 'application/octet-stream', { length: 300 }));

  const [item] = tracker.list(7);
  assert.equal(item.kind, 'media');
  assert.equal(item.duration, 4);
  assert.equal(item.referrer, 'https://player.example/embed/42');
  assert.deepEqual(read, ['https://p.example/p/9f3a']);
});

test('something that is not a playlist is read once and then forgotten; a tab has a limit', async () => {
  const read = [];
  const tracker = new MediaTracker({ fetchText: async (url) => { read.push(url); return '{"not":"a playlist"}'; } });
  await tracker.consider(response('https://p.example/a', 'text/plain'));
  await tracker.consider(response('https://p.example/a', 'text/plain'));
  assert.equal(tracker.count(7), 0);
  assert.equal(read.length, 1);

  for (let i = 0; i < 100; i++) await tracker.consider(response(`https://p.example/n/${i}`, 'text/plain'));
  assert.ok(read.length <= 40, `read ${read.length}`);
});

test('a named playlist (.m3u8) is still found without the extra rules', async () => {
  const tracker = new MediaTracker({ fetchText: async () => PLAYLIST });
  await tracker.consider(response('https://p.example/x/index.m3u8', 'application/vnd.apple.mpegurl'));
  assert.equal(tracker.count(7), 1);
});

// ---- the extension's own look, with the player's headers --------------------------------------------------------------

function fakeNet() {
  const calls = [];
  const chromeApi = {
    runtime: { id: 'abcdefgh' },
    declarativeNetRequest: { updateSessionRules: async (change) => { calls.push(change); } },
  };
  return { calls, chromeApi };
}
const recorded = [{ name: 'Referer', value: 'https://player.example/' }, { name: 'Origin', value: 'https://player.example' }, { name: 'Cookie', value: 'a=1' }];

test('the look carries the player\'s Referer and Origin for that one request, then the rule is gone', async () => {
  const { calls, chromeApi } = fakeNet();
  let during;
  const text = await defaultFetchText('https://p.example/p/9f3a?x=1&y=(2)', recorded, chromeApi, async () => {
    during = calls.length;
    return { ok: true, text: async () => PLAYLIST };
  });

  assert.equal(text, PLAYLIST);
  assert.equal(during, 1);                                 // the rule was in place when the request went out
  const rule = calls[0].addRules[0];
  assert.deepEqual(rule.action.requestHeaders, [
    { header: 'referer', operation: 'set', value: 'https://player.example/' },
    { header: 'origin', operation: 'set', value: 'https://player.example' },
  ]);
  assert.deepEqual(rule.condition.initiatorDomains, ['abcdefgh']);   // only requests of the extension itself
  assert.equal(new RegExp(rule.condition.regexFilter).test('https://p.example/p/9f3a?x=1&y=(2)'), true);   // exactly this address ...
  assert.equal(new RegExp(rule.condition.regexFilter).test('https://p.example/p/9f3aXx=1&y=(2)'), false);  // ... and no other
  assert.deepEqual(calls[1].removeRuleIds, [rule.id]);
});

test('the rule is removed when the request fails, and nothing is set when there is nothing to replay', async () => {
  const { calls, chromeApi } = fakeNet();
  await assert.rejects(defaultFetchText('https://p.example/a', recorded, chromeApi, async () => { throw new Error('offline'); }), /offline/);
  assert.equal(calls.length, 2);
  assert.ok(calls[1].removeRuleIds);

  const quiet = fakeNet();
  await defaultFetchText('https://p.example/a', [{ name: 'Cookie', value: 'a=1' }], quiet.chromeApi, async () => ({ ok: true, text: async () => 'x' }));
  assert.equal(quiet.calls.length, 0);

  const none = await defaultFetchText('https://p.example/a', recorded, { runtime: { id: 'x' } }, async () => ({ ok: false, text: async () => '' }));
  assert.equal(none, '');   // no declarativeNetRequest: the plain request is made
});

test('the controller gives the tracker the headers the player sent for that address', async () => {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  const recorder = new HeaderRecorder(fake.chrome);
  recorder.record('https://p.example/p/9f3a', recorded);
  const seen = [];
  controller = new Controller({ chrome: fake.chrome, link, recorder, userAgent: 'UA', timers, fetchText: async (url, headers) => { seen.push([url, headers]); return PLAYLIST; } });

  await controller.media.consider(response('https://p.example/p/9f3a', 'application/octet-stream', { length: 300 }));
  assert.equal(seen[0][0], 'https://p.example/p/9f3a');
  assert.equal(seen[0][1].find((h) => h.name === 'Referer').value, 'https://player.example/');
});

test('a stream is sent with the player\'s page as referrer, not the outer page', async () => {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  Object.assign(fake.chrome.tabs.active, { url: 'https://blog.example/post/1', title: 'A blog' });
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'UA', timers, fetchText: async () => PLAYLIST });
  controller.registerListeners();

  await controller.media.consider(response('https://p.example/p/9f3a', 'application/octet-stream', { length: 300 }));
  const sent = await controller.sendMedia('https://p.example/p/9f3a', fake.chrome.tabs.active);
  assert.equal(sent.accepted, true);
  assert.equal(app.captures[0].referrer, 'https://player.example/embed/42');
  assert.equal(app.captures[0].source, 'media');
});

test('the extension\'s own look at a playlist does not replace the headers the player sent', () => {
  const fake = fakeChrome(new FakeApp());
  fake.chrome.runtime.id = 'abcdefgh';
  const recorder = new HeaderRecorder(fake.chrome);
  const send = (details) => fake.chrome.webRequest.onBeforeSendHeaders.listeners.forEach((fn) => fn(details));

  send({ url: 'https://p.example/p/1', initiator: 'https://player.example', requestHeaders: [{ name: 'Referer', value: 'https://player.example/embed/42' }] });
  send({ url: 'https://p.example/p/1', initiator: 'chrome-extension://abcdefgh', requestHeaders: [{ name: 'Accept', value: '*/*' }] });

  assert.equal(recorder.lookup('https://p.example/p/1').find((h) => h.name === 'Referer').value, 'https://player.example/embed/42');
});

test('the pieces are asked for with the full address of the player\'s page, found from the frame that reported its video', async () => {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  Object.assign(fake.chrome.tabs.active, { url: 'https://blog.example/post/1', title: 'A blog' });
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'UA', timers, fetchText: async () => PLAYLIST });
  controller.registerListeners();

  // the browser only says which ORIGIN the request came from ...
  await controller.media.consider({ ...response('https://p.example/p/9f3a', 'application/octet-stream', { length: 300 }), documentUrl: undefined, initiator: 'https://player.example' });
  // ... the frame that reported its video says the whole address
  await controller.onPageVideos({ tab: fake.chrome.tabs.active, frameId: 3, url: 'https://player.example/embed/42?autoplay=1' }, [{ title: 'Film', src: 'blob:https://player.example/z', blob: true }]);

  await controller.sendMedia('https://p.example/p/9f3a', fake.chrome.tabs.active);
  assert.equal(app.captures[0].referrer, 'https://player.example/embed/42?autoplay=1');
});
