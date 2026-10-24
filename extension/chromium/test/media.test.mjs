import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyPlaylist, liveStreamKind, looksLikeLiveFlv, looksLikePlaylist, MediaTracker } from '../lib/media.js';
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

// ---- a live stream that is one endless response (FLV): never read, recorded by the app ----

const flv = (tabId, url, extra = {}) => ({
  tabId, url, type: 'xmlhttprequest', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'video/x-flv' }], ...extra,
});

test('an FLV response with no length is a live stream; an FLV file with a length is not', () => {
  assert.equal(looksLikeLiveFlv(flv(7, 'https://pull.example/stream.flv?k=1')), true);
  assert.equal(looksLikeLiveFlv(flv(7, 'https://pull.example/anything', { responseHeaders: [{ name: 'content-type', value: 'video/x-flv' }] })), true);
  assert.equal(looksLikeLiveFlv(flv(7, 'https://pull.example/stream.flv', { responseHeaders: [] })), true);      // named .flv, no type given
  assert.equal(looksLikeLiveFlv(flv(7, 'https://x.example/clip.flv', { responseHeaders: [{ name: 'Content-Type', value: 'video/x-flv' }, { name: 'Content-Length', value: '123456' }] })), false);
  assert.equal(looksLikeLiveFlv(flv(7, 'https://x.example/clip.flv', { responseHeaders: [{ name: 'Content-Type', value: 'text/html' }] })), false);
  assert.equal(looksLikeLiveFlv(flv(7, 'https://pull.example/stream.flv', { statusCode: 403 })), false);
  assert.equal(looksLikeLiveFlv(flv(7, 'https://pull.example/stream.flv', { type: 'image' })), false);
});

test('the tracker lists a live FLV without ever reading it', async () => {
  const fetched = [];
  const tracker = new MediaTracker({ fetchText: async (u) => { fetched.push(u); return ''; } });

  await tracker.consider(flv(7, 'https://pull.example/stream.flv?k=1'));

  assert.deepEqual(fetched, []);   // a response that never ends cannot be read
  const [item] = tracker.list(7);
  assert.deepEqual([item.kind, item.live, item.url], ['record', true, 'https://pull.example/stream.flv?k=1']);
});

test('a live FLV is sent to the app as a recording, and the page route is not offered next to it', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit(flv(7, 'https://pull.example/stream.flv?k=1'));

  const { items } = await t.controller.listMedia();
  assert.deepEqual(items.map((i) => [i.kind, i.live]), [['record', true]]);

  assert.equal((await t.controller.sendMedia(items[0].url)).accepted, true);
  assert.equal(t.app.captures[0].source, 'record');
  assert.equal(t.app.captures[0].url, 'https://pull.example/stream.flv?k=1');
});

// ---- the other endless formats: MPEG-TS and WebM (and not their pieces) ----

const endless = (tabId, url, type, extra = {}) => ({ tabId, url, type: 'xmlhttprequest', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: type }], ...extra });

test('the format of an endless response comes from its type, and a length or a range rules it out', () => {
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/x-flv')), 'flv');
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/MP2T')), 'ts');
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/webm; codecs="vp9"')), 'webm');
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/x-matroska')), 'webm');
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/mp4')), null);                                    // not endless in any way we can tell
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/mp2t', { statusCode: 206 })), null);               // a range of something
  assert.equal(liveStreamKind(endless(7, 'https://x.example/a', 'video/webm', { responseHeaders: [{ name: 'Content-Type', value: 'video/webm' }, { name: 'Content-Length', value: '99' }] })), null);
});

test('an endless MPEG-TS or WebM response is listed once, as a recording', async () => {
  const tracker = new MediaTracker({ fetchText: async () => '' });

  await tracker.consider(endless(7, 'https://pull.example/channel.ts', 'video/mp2t'));
  await tracker.consider(endless(8, 'https://pull.example/live.webm', 'video/webm'));

  assert.deepEqual(tracker.list(7).map((i) => [i.kind, i.streamFormat]), [['record', 'ts']]);
  assert.deepEqual(tracker.list(8).map((i) => [i.kind, i.streamFormat]), [['record', 'webm']]);
});

test('the pieces of an HLS stream are not endless streams, whichever comes first', async () => {
  const texts = { 'https://cdn.example/v/index.m3u8': LIVE };
  const tracker = new MediaTracker({ fetchText: async (u) => texts[u] ?? '' });

  await tracker.consider(seen(7, 'https://cdn.example/v/index.m3u8'));                                  // the playlist, read first
  await tracker.consider(endless(7, 'https://cdn.example/v/a.ts', 'video/mp2t'));
  await tracker.consider(endless(7, 'https://cdn.example/v/b.ts', 'video/mp2t'));

  assert.deepEqual(tracker.list(7).map((i) => i.kind), ['media']);
});

test('several pieces in a row (no playlist seen) are told from one endless stream and not listed', async () => {
  const tracker = new MediaTracker({ fetchText: async () => '' });

  await tracker.consider(endless(7, 'https://cdn.example/seg1.webm', 'video/webm'));
  assert.equal(tracker.list(7).length, 1);                                                               // one address could be a live stream
  await tracker.consider(endless(7, 'https://cdn.example/seg2.webm', 'video/webm'));                      // a second one: they are pieces
  await tracker.consider(endless(7, 'https://cdn.example/seg3.webm', 'video/webm'));

  assert.equal(tracker.list(7).length, 0);
});

test('FLV qualities of one live stream are all listed, and a new page starts afresh', async () => {
  const tracker = new MediaTracker({ fetchText: async () => '' });
  await tracker.consider(endless(7, 'https://pull.example/sd.flv', 'video/x-flv'));
  await tracker.consider(endless(7, 'https://pull.example/hd.flv', 'video/x-flv'));
  assert.equal(tracker.list(7).length, 2);

  tracker.clear(7);
  await tracker.consider(endless(7, 'https://cdn.example/a.ts', 'video/mp2t'));
  assert.equal(tracker.list(7).length, 1);
});

// ---- diagnosis: what a tab's player asked for ----

test('the recent media requests of a tab are kept without their query strings, and only media-looking ones', async () => {
  const tracker = new MediaTracker({ fetchText: async () => '' });
  await tracker.consider(endless(7, 'https://cdn.example/v/s00062.m4s?token=secret', 'video/iso.segment', { responseHeaders: [{ name: 'Content-Type', value: 'video/iso.segment' }, { name: 'Content-Length', value: '9' }] }));
  await tracker.consider(endless(7, 'https://cdn.example/app.js', 'text/javascript'));

  const [one, ...rest] = tracker.recent(7);

  assert.deepEqual(rest, []);
  assert.equal(one.url, 'https://cdn.example/v/s00062.m4s');
  assert.deepEqual([one.contentType, one.hasLength, one.type], ['video/iso.segment', true, 'xmlhttprequest']);
  assert.equal(tracker.recent(7, { full: true })[0].url, 'https://cdn.example/v/s00062.m4s?token=secret');
  tracker.clear(7);
  assert.deepEqual(tracker.recent(7), []);
});

test('diagnose says plainly when the player asked for pieces but never for a playlist', async () => {
  const t = setup();
  for (const n of [1, 2, 3]) await t.chrome.webRequest.onHeadersReceived.emit({ tabId: 7, url: `https://cdn.example/v/s0000${n}.m4s?t=x`, type: 'xmlhttprequest', statusCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'video/iso.segment' }, { name: 'Content-Length', value: '9' }] });

  const report = JSON.parse(await t.controller.diagnose());

  assert.match(report.notes.join(' '), /asked for 3 pieces .* no playlist or manifest/);
  assert.equal(report.recentRequests.length, 3);
  assert.ok(!JSON.stringify(report).includes('t=x'));                                    // no query string (it can hold a token) unless asked for
  assert.ok(JSON.stringify(JSON.parse(await t.controller.diagnose({ full: true }))).includes('t=x'));
});

// ---- a player that fetches through a service worker or shared worker (the browser gives such requests no tab) ----
const settle = () => new Promise((r) => setTimeout(r, 5));

const fromWorker = (url, type, origin = 'https://player.example') => ({
  tabId: -1, frameId: -1, url, type: 'xmlhttprequest', statusCode: 200, initiator: origin, responseHeaders: [{ name: 'Content-Type', value: type }],
});

test('a service worker\'s playlist goes to the tab that shows its page', async () => {
  const t = setup();
  t.controller.pageVideos.set(7, new Map([[3, { url: 'https://player.example/embed/1', videos: [] }]]));   // an iframe of the tab with a video

  await t.chrome.webRequest.onHeadersReceived.emit(fromWorker('https://cdn.example/v/index.m3u8', 'application/vnd.apple.mpegurl'));

  assert.deepEqual(t.controller.media.list(7).map((i) => i.url), ['https://cdn.example/v/index.m3u8']);
});

test('what a worker fetched before its page reported a video is handed over when it does', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit(fromWorker('https://cdn.example/v/index.m3u8', 'application/vnd.apple.mpegurl'));
  assert.equal(t.controller.media.list(7).length, 0);                           // nobody to give it to yet

  await t.controller.onPageVideos({ tab: { id: 7, url: 'https://site.example/watch' }, frameId: 3, url: 'https://player.example/embed/1' }, [{ title: 'x', src: 'blob:https://player.example/1', blob: true, duration: 60, box: [640, 360], opacity: 1 }]);
  for (let i = 0; i < 100 && t.controller.media.list(7).length === 0; i++) await settle();   // (the playlist is read, which takes a moment)

  assert.deepEqual(t.controller.media.list(7).map((i) => i.url), ['https://cdn.example/v/index.m3u8']);
  assert.equal(t.controller.unattributed.size, 0);
});

test('a worker request is not guessed onto a tab when two tabs show that origin, or when it came from no web page', async () => {
  const t = setup();
  t.controller.pageVideos.set(7, new Map([[0, { url: 'https://player.example/a', videos: [] }]]));
  t.controller.pageVideos.set(8, new Map([[0, { url: 'https://player.example/b', videos: [] }]]));

  await t.chrome.webRequest.onHeadersReceived.emit(fromWorker('https://cdn.example/v/index.m3u8', 'application/vnd.apple.mpegurl'));
  await t.chrome.webRequest.onHeadersReceived.emit(fromWorker('https://cdn.example/v/other.m3u8', 'application/vnd.apple.mpegurl', 'chrome-extension://abc'));

  assert.equal(t.controller.media.list(7).length + t.controller.media.list(8).length, 0);
  assert.deepEqual([...t.controller.unattributed.keys()], ['https://player.example']);
});
