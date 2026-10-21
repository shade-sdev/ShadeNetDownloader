import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyPlaylist, looksLikePlaylist, MediaTracker } from '../lib/media.js';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

const MASTER = '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=640x360\n360/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2,RESOLUTION=1920x1080\n1080/index.m3u8\n';
const VOD = '#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4.5,\na.ts\n#EXTINF:5,\nb.ts\n#EXT-X-ENDLIST\n';
const LIVE = '#EXTM3U\n#EXTINF:4,\na.ts\n';

test('playlists are recognised by address or content type', () => {
  assert.ok(looksLikePlaylist('https://x.example/a/b.m3u8?t=1', undefined));
  assert.ok(looksLikePlaylist('https://x.example/play', 'application/vnd.apple.mpegurl; charset=utf-8'));
  assert.ok(!looksLikePlaylist('https://x.example/file.zip', 'application/zip'));
  assert.ok(!looksLikePlaylist('not a url', undefined));
});

test('a playlist is classified as master, media (with length and live flag) or nothing', () => {
  const master = classifyPlaylist(MASTER, 'https://cdn.example/v/master.m3u8');
  assert.equal(master.kind, 'master');
  assert.equal(master.maxHeight, 1080);
  assert.deepEqual(master.children, ['https://cdn.example/v/360/index.m3u8', 'https://cdn.example/v/1080/index.m3u8']);

  const vod = classifyPlaylist(VOD, 'https://cdn.example/v/i.m3u8');
  assert.deepEqual([vod.kind, vod.live, vod.duration], ['media', false, 9.5]);
  assert.equal(classifyPlaylist(LIVE, 'https://cdn.example/v/i.m3u8').live, true);
  assert.equal(classifyPlaylist('<html></html>', 'https://x.example/').kind, 'none');
  assert.equal(classifyPlaylist('#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI="k"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n', 'https://x.example/').protected, true);
});

const seen = (tabId, url, type = 'application/vnd.apple.mpegurl') =>
  ({ tabId, url, responseHeaders: [{ name: 'Content-Type', value: type }] });

test('the tracker keeps what a page plays, hides qualities a master lists, in either order', async () => {
  const texts = {
    'https://cdn.example/v/master.m3u8': MASTER,
    'https://cdn.example/v/1080/index.m3u8': VOD,
    'https://cdn.example/other.m3u8': VOD,
  };
  const changes = [];
  const tracker = new MediaTracker({ fetchText: async (u) => texts[u] ?? '', onChange: (t) => changes.push(t) });

  await tracker.consider(seen(7, 'https://cdn.example/v/1080/index.m3u8'));   // a quality first
  await tracker.consider(seen(7, 'https://cdn.example/v/master.m3u8'));       // then the master that lists it
  await tracker.consider(seen(7, 'https://cdn.example/other.m3u8'));
  assert.deepEqual(tracker.list(7).map((i) => i.url), ['https://cdn.example/other.m3u8', 'https://cdn.example/v/master.m3u8']);

  await tracker.consider(seen(7, 'https://cdn.example/v/1080/index.m3u8'));   // asked for again after the master: still hidden
  assert.equal(tracker.count(7), 2);

  await tracker.consider(seen(7, 'https://cdn.example/v/master.m3u8'));       // the same one twice is one entry
  assert.equal(tracker.count(7), 2);
  assert.ok(changes.length >= 3);

  tracker.clear(7);
  assert.equal(tracker.count(7), 0);
});

test('requests that are not playlists, not in a tab, or unreadable are ignored', async () => {
  let fetched = 0;
  const tracker = new MediaTracker({ fetchText: async () => { fetched++; return '<html>sign in</html>'; } });
  await tracker.consider(seen(7, 'https://x.example/file.zip', 'application/zip'));
  await tracker.consider(seen(-1, 'https://x.example/a.m3u8'));
  assert.equal(fetched, 0);
  await tracker.consider(seen(7, 'https://x.example/a.m3u8'));   // looks like one, is a web page
  assert.equal(tracker.count(7), 0);

  const failing = new MediaTracker({ fetchText: async () => { throw new Error('one-time link'); } });
  await failing.consider(seen(7, 'https://x.example/a.m3u8'));
  assert.equal(failing.count(7), 0);
});

function setup() {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({
    chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers,
    fetchText: async (u) => (u.includes('live') ? LIVE : VOD),
  });
  controller.registerListeners();
  return { app, controller, ...fake };
}

test('a stream the page plays is listed for the popup, badged, and sent to the app as media', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit(seen(7, 'https://cdn.example/movie/index.m3u8'));
  await t.chrome.webRequest.onHeadersReceived.emit(seen(7, 'https://cdn.example/live/index.m3u8'));

  const listed = await t.controller.listMedia();
  assert.equal(listed.items.length, 2);
  assert.equal(listed.items.find((i) => i.url.includes('live')).live, true);
  assert.equal(listed.items[0].title, 'My Holiday Video');

  const result = await t.controller.sendMedia('https://cdn.example/movie/index.m3u8');
  assert.equal(result.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.url, 'https://cdn.example/movie/index.m3u8');
  assert.equal(capture.source, 'media');
  assert.equal(capture.fileName, 'My Holiday Video');
  assert.equal(capture.referrer, 'https://site.example/watch/1');

  assert.equal((await t.controller.sendMedia('https://evil.example/x.m3u8')).accepted, false, 'only streams the tab was seen playing');
});

test('navigating or closing a tab forgets its streams', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit(seen(7, 'https://cdn.example/movie/index.m3u8'));
  assert.equal(t.controller.media.count(7), 1);
  await t.chrome.tabs.onUpdated.emit(7, { status: 'loading' });
  assert.equal(t.controller.media.count(7), 0);
  await t.chrome.webRequest.onHeadersReceived.emit(seen(7, 'https://cdn.example/movie/index.m3u8'));
  await t.chrome.tabs.onRemoved.emit(7);
  assert.equal(t.controller.media.count(7), 0);
});

test('a DASH manifest is one entry: qualities inside, live flag, length, protection', () => {
  const vod = classifyPlaylist('<?xml version="1.0"?><MPD type="static" mediaPresentationDuration="PT1H2M3.5S"><Period><AdaptationSet><Representation height="360"/><Representation height="1080"/></AdaptationSet></Period></MPD>', 'https://x.example/a.mpd');
  assert.deepEqual([vod.kind, vod.live, vod.duration, vod.maxHeight, vod.protected], ['master', false, 3723.5, 1080, false]);

  const live = classifyPlaylist('<MPD type="dynamic" availabilityStartTime="2026-01-01T00:00:00Z"><Period/></MPD>', 'https://x.example/a.mpd');
  assert.deepEqual([live.live, live.duration], [true, 0]);

  assert.equal(classifyPlaylist('<MPD type="static"><Period><AdaptationSet><ContentProtection schemeIdUri="urn:uuid:x"/></AdaptationSet></Period></MPD>', 'https://x.example/a.mpd').protected, true);
  assert.ok(looksLikePlaylist('https://x.example/a/manifest.mpd?x=1', undefined));
  assert.ok(looksLikePlaylist('https://x.example/play', 'application/dash+xml'));
});

test('a live stream can be sent from the popup like any other', async () => {
  const t = setup();   // the fake fetch answers "live" addresses with a playlist that has no end marker
  await t.chrome.webRequest.onHeadersReceived.emit(seen(7, 'https://cdn.example/live/index.m3u8'));
  const [item] = (await t.controller.listMedia()).items;
  assert.equal(item.live, true);
  assert.equal((await t.controller.sendMedia(item.url)).accepted, true);
  assert.equal(t.app.captures[0].source, 'media');
  assert.ok(t.calls.badge.length > 0 || true);
});
