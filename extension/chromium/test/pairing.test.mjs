// A player that asks for the picture's playlist and the sound's playlist on their own leaves two entries that belong together:
// they become one, which downloads both.
import test from 'node:test';
import assert from 'node:assert/strict';
import { MediaTracker, classifyPlaylist, pairStreams } from '../lib/media.js';
import { Controller, defaultFetchHead } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';
import * as real from './fixtures.mjs';

const item = (url, kind, duration, extra = {}) => ({ url, kind: 'media', live: false, duration, maxHeight: 0, tracks: { kind, width: 0, height: extra.height ?? 0 }, ...extra });
const PLAYLIST = (map) => `#EXTM3U\n#EXT-X-VERSION:7\n${map ? `#EXT-X-MAP:URI="${map}"\n` : ''}#EXTINF:2.0,\nseg0.m4s\n#EXTINF:2.0,\nseg1.m4s\n#EXT-X-ENDLIST\n`;

test('picture alone and sound alone become one entry that names both', () => {
  const shown = pairStreams([item('https://c.example/a/index.m3u8', 'audio', 120), item('https://c.example/v/index.m3u8', 'video', 120, { height: 720 })]);
  assert.equal(shown.length, 1);
  assert.equal(shown[0].url, 'https://c.example/v/index.m3u8');
  assert.equal(shown[0].audioUrl, 'https://c.example/a/index.m3u8');
  assert.equal(shown[0].maxHeight, 720);
});

test('several qualities of picture each get the sound; the sound is not listed on its own', () => {
  const shown = pairStreams([
    item('https://c.example/v1080', 'video', 100, { height: 1080 }), item('https://c.example/a', 'audio', 100), item('https://c.example/v720', 'video', 100, { height: 720 }),
  ]);
  assert.deepEqual(shown.map((i) => [i.url, i.audioUrl]), [['https://c.example/v1080', 'https://c.example/a'], ['https://c.example/v720', 'https://c.example/a']]);
});

test('a sound of another length is not this picture\'s; the closest one is chosen', () => {
  const shown = pairStreams([item('https://c.example/a-other', 'audio', 3000), item('https://c.example/v', 'video', 100), item('https://c.example/a-near', 'audio', 101)]);
  assert.equal(shown.find((i) => i.url === 'https://c.example/v').audioUrl, 'https://c.example/a-near');
  assert.equal(shown.find((i) => i.url === 'https://c.example/a-other').audioOnly, true);   // left alone, and called what it is
  assert.equal(shown.length, 2);

  const none = pairStreams([item('https://c.example/a', 'audio', 3000), item('https://c.example/v', 'video', 100)]);
  assert.equal(none.length, 2);
  assert.equal(none.find((i) => i.url === 'https://c.example/v').audioUrl, undefined);
});

test('a stream that holds both, an unknown one, and a lone picture or sound stay as they are', () => {
  const both = pairStreams([item('https://c.example/m', 'both', 60), item('https://c.example/u', 'unknown', 60)]);
  assert.deepEqual(both.map((i) => i.audioUrl), [undefined, undefined]);
  assert.equal(pairStreams([item('https://c.example/v', 'video', 60)])[0].audioUrl, undefined);
  assert.equal(pairStreams([item('https://c.example/a', 'audio', 60)])[0].audioOnly, true);
  assert.equal(pairStreams([{ url: 'x', kind: 'master', children: [] }])[0].audioOnly, undefined);   // a master playlist is never touched
});

test('a live picture and a live sound (no length) pair too', () => {
  const shown = pairStreams([item('https://c.example/v', 'video', 0, { live: true }), item('https://c.example/a', 'audio', 0, { live: true })]);
  assert.equal(shown.length, 1);
  assert.equal(shown[0].audioUrl, 'https://c.example/a');
});

// ---- the tracker looks inside the first piece ------------------------------------------------------------------------

test('the first piece of a playlist is named: the MP4 initialisation part, else the first segment', () => {
  assert.equal(classifyPlaylist(PLAYLIST('init.mp4'), 'https://c.example/v/index.m3u8').piece, 'https://c.example/v/init.mp4');
  assert.equal(classifyPlaylist(PLAYLIST(null), 'https://c.example/v/index.m3u8').piece, 'https://c.example/v/seg0.m4s');
});

test('the tracker asks what is inside and remembers it', async () => {
  const asked = [];
  const bytesFor = { 'https://c.example/v/init.mp4': real.fmp4Video, 'https://c.example/a/init.mp4': real.fmp4Audio };
  const tracker = new MediaTracker({
    fetchText: async (url) => PLAYLIST(url.includes('/v/') ? 'init.mp4' : 'init.mp4'),
    fetchHead: async (piece, via) => { asked.push([piece, via]); return bytesFor[piece]; },
  });
  const seen = (url) => tracker.consider({ tabId: 7, url, type: 'xmlhttprequest', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  await seen('https://c.example/v/index.m3u8');
  await seen('https://c.example/a/index.m3u8');

  assert.deepEqual(asked, [['https://c.example/v/init.mp4', 'https://c.example/v/index.m3u8'], ['https://c.example/a/init.mp4', 'https://c.example/a/index.m3u8']]);
  assert.equal(tracker.get(7, 'https://c.example/v/index.m3u8').tracks.kind, 'video');
  assert.equal(tracker.get(7, 'https://c.example/v/index.m3u8').tracks.height, 240);
  assert.equal(tracker.get(7, 'https://c.example/a/index.m3u8').tracks.kind, 'audio');
});

test('a piece that cannot be read leaves the stream listed, as "unknown"', async () => {
  const tracker = new MediaTracker({ fetchText: async () => PLAYLIST('init.mp4'), fetchHead: async () => { throw new Error('403'); } });
  await tracker.consider({ tabId: 7, url: 'https://c.example/x/index.m3u8', type: 'xmlhttprequest', statusCode: 200, responseHeaders: [] });
  assert.equal(tracker.get(7, 'https://c.example/x/index.m3u8').tracks.kind, 'unknown');
});

// ---- the first bytes of a piece, with the player's headers ------------------------------------------------------------

function fakeNet() {
  const calls = [];
  return { calls, chromeApi: { runtime: { id: 'abcdefgh' }, declarativeNetRequest: { updateSessionRules: async (c) => { calls.push(c); } } } };
}
const recorded = [{ name: 'Referer', value: 'https://player.example/embed/1' }];
const streaming = (parts) => ({
  ok: true,
  body: { getReader: () => { let i = 0; return { read: async () => (i < parts.length ? { done: false, value: parts[i++] } : { done: true }), cancel: async () => { this.cancelled = true; } }; } },
});

test('the first bytes are asked for with a range and the player\'s Referer, and only the first bytes are kept', async () => {
  const { calls, chromeApi } = fakeNet();
  let init;
  const bytes = await defaultFetchHead('https://c.example/v/init.mp4', recorded, chromeApi, async (url, options) => {
    init = options;
    return streaming([new Uint8Array(40).fill(1), new Uint8Array(40).fill(2), new Uint8Array(40).fill(3)]);
  }, 64);

  assert.equal(init.headers.Range, 'bytes=0-63');
  assert.equal(bytes.length, 64);                           // a server that ignored the range is cut off
  assert.deepEqual([bytes[0], bytes[39], bytes[40], bytes[63]], [1, 1, 2, 2]);
  assert.equal(calls[0].addRules[0].action.requestHeaders[0].value, 'https://player.example/embed/1');
  assert.ok(calls[1].removeRuleIds);
  assert.equal((await defaultFetchHead('https://c.example/x', recorded, chromeApi, async () => ({ ok: false }))).length, 0);
});

// ---- the whole thing, in the extension --------------------------------------------------------------------------------

function setup() {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  Object.assign(fake.chrome.tabs.active, { url: 'https://site.example/watch/1', title: 'The film - Site' });
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  const bytesFor = { 'https://c.example/v/init.mp4': real.fmp4Video, 'https://c.example/a/init.mp4': real.fmp4Audio };
  controller = new Controller({
    chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'UA', timers,
    fetchText: async () => PLAYLIST('init.mp4'), fetchHead: async (piece) => bytesFor[piece],
  });
  controller.registerListeners();
  const seen = (url) => controller.media.consider({ tabId: 7, url, type: 'xmlhttprequest', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }], documentUrl: 'https://site.example/watch/1' });
  return { app, controller, seen, ...fake };
}

test('two playlists, one list entry: the toolbar says 1 video, and one click sends both', async () => {
  const t = setup();
  await t.seen('https://c.example/v/index.m3u8');
  await t.seen('https://c.example/a/index.m3u8');
  await new Promise((r) => setTimeout(r, 5));

  const { items } = await t.controller.listMedia();
  assert.equal(items.length, 1);
  assert.equal(items[0].url, 'https://c.example/v/index.m3u8');
  assert.equal(items[0].audioUrl, 'https://c.example/a/index.m3u8');
  assert.equal(items[0].maxHeight, 240);
  assert.equal(items[0].title, 'The film - Site');
  assert.ok(t.calls.badge.includes('1'));
  assert.ok(!t.calls.badge.includes('2'));
  assert.match(t.calls.titles.at(-1), /1 video can be downloaded/);

  const sent = await t.controller.sendMedia('https://c.example/v/index.m3u8', t.chrome.tabs.active);
  assert.equal(sent.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.url, 'https://c.example/v/index.m3u8');
  assert.equal(capture.audioUrl, 'https://c.example/a/index.m3u8');
  assert.equal(capture.source, 'media');
  assert.equal(capture.fileName, 'The film - Site');
});

test('a stream that is only sound is listed as sound, and a plain one is sent without a second address', async () => {
  const t = setup();
  await t.seen('https://c.example/a/index.m3u8');
  const { items } = await t.controller.listMedia();
  assert.equal(items.length, 1);
  assert.equal(items[0].audioOnly, true);

  const u = setup();
  await u.seen('https://c.example/v/index.m3u8');
  await u.controller.sendMedia('https://c.example/v/index.m3u8', u.chrome.tabs.active);
  assert.equal(u.app.captures[0].audioUrl, undefined);
});
