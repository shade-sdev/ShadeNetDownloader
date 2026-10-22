import test from 'node:test';
import assert from 'node:assert/strict';
import { cleanTitle, describeTracks, extensionFor, parsePlayback, TracksTracker } from '../lib/youtube.js';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

const base = 'https://rr2---sn-abc.googlevideo.com/videoplayback';
const video = `${base}?expire=1&ei=x&id=o-1&itag=137&mime=video%2Fmp4&clen=48000000&dur=212.5&sig=SIG&n=solved&range=0-1048575&rn=3&rbuf=9000`;
const audio = `${base}?expire=1&ei=x&id=o-1&itag=140&mime=audio%2Fmp4&clen=3400000&dur=212.5&sig=SIG2&range=0-65535&rn=4&rbuf=1000`;

test('a playback request is understood and its slice parameters are dropped', () => {
  const v = parsePlayback(video);
  assert.deepEqual([v.kind, v.itag, v.mime, v.clen, v.dur, v.live, v.drm], ['video', 137, 'video/mp4', 48000000, 212.5, false, false]);
  const u = new URL(v.url);
  for (const dropped of ['range', 'rn', 'rbuf']) assert.ok(!u.searchParams.has(dropped), dropped);
  for (const kept of ['sig', 'n', 'expire', 'id']) assert.ok(u.searchParams.has(kept), kept);   // the signature stays: the browser made it
  assert.equal(v.source, video);

  assert.equal(parsePlayback(audio).kind, 'audio');
  assert.equal(parsePlayback(`${base}?itag=18&mime=video%2Fmp4&clen=1`).kind, 'muxed');
  assert.equal(parsePlayback(`${base}?itag=137&mime=video%2Fmp4&sq=12`).live, true);
  assert.equal(parsePlayback(`${base}?itag=137&mime=video%2Fmp4&drm_families=widevine`).drm, true);
});

test('other requests are not playback requests', () => {
  assert.equal(parsePlayback('https://www.youtube.com/watch?v=abc'), null);
  assert.equal(parsePlayback('https://evil.example/videoplayback?itag=137&mime=video%2Fmp4'), null);
  assert.equal(parsePlayback('https://x.googlevideo.com/other?itag=1&mime=video%2Fmp4'), null);
  assert.equal(parsePlayback(`${base}?itag=x&mime=text%2Fplain`), null);
  assert.equal(parsePlayback('not a url'), null);
});

test('the tracker keeps what is playing: the latest picture and sound, never sound alone', () => {
  const changes = [];
  const tracker = new TracksTracker({ onChange: (t) => changes.push(t) });
  const seen = (url, statusCode = 206) => tracker.consider({ tabId: 7, url, statusCode });

  seen(audio);
  assert.equal(tracker.current(7), null);                 // sound alone is not offered
  seen(video);
  seen(video.replace('rn=3', 'rn=4'));                    // the next slice of the same file: nothing new
  assert.equal(changes.filter((t) => t === 7).length, 2);
  const now = tracker.current(7);
  assert.equal(now.video.itag, 137);
  assert.equal(now.audio.itag, 140);

  seen(video.replace('itag=137', 'itag=136'));            // the user chose another quality
  assert.equal(tracker.current(7).video.itag, 136);

  tracker.consider({ tabId: 7, url: video, statusCode: 403 });   // a refused request is not playing
  assert.equal(tracker.current(7).video.itag, 136);

  tracker.clear(7);
  assert.equal(tracker.current(7), null);
});

test('a file with picture and sound together needs no second track', () => {
  const tracker = new TracksTracker();
  tracker.consider({ tabId: 1, url: `${base}?itag=18&mime=video%2Fmp4&clen=5&dur=10`, statusCode: 200 });
  tracker.consider({ tabId: 1, url: audio, statusCode: 206 });
  assert.equal(tracker.current(1).audio, undefined);
});

test('labels, titles and containers', () => {
  assert.equal(describeTracks({ video: parsePlayback(video), audio: parsePlayback(audio) }), '1080p + 128 kbit/s sound');
  assert.equal(cleanTitle('(2) My: Holiday/Video? - YouTube'), 'My Holiday Video');
  assert.equal(cleanTitle('How to share with just friends. | Facebook'), 'How to share with just friends.');
  assert.equal(cleanTitle('Sunrise - Instagram'), 'Sunrise');
  assert.equal(cleanTitle('Facebook marketing | tips'), 'Facebook marketing tips');
  assert.equal(cleanTitle(''), 'video');
  assert.equal(extensionFor({ video: parsePlayback(video), audio: parsePlayback(audio) }), '.mp4');
  assert.equal(extensionFor({ video: parsePlayback(video.replace('video%2Fmp4', 'video%2Fwebm')), audio: parsePlayback(audio) }), '.mkv');
});

function setup() {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  fake.chrome.tabs.active = { id: 7, title: 'My Holiday Video - YouTube', url: 'https://www.youtube.com/watch?v=abc' };
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  const recorder = new HeaderRecorder(fake.chrome);
  controller = new Controller({ chrome: fake.chrome, link, recorder, userAgent: 'Test-UA/1', timers, fetchText: async () => '' });
  controller.registerListeners();
  return { app, controller, recorder, ...fake };
}

test('what a YouTube tab is playing is listed and sent to the app as two files', async () => {
  const t = setup();
  t.recorder.record(video, [{ name: 'Origin', value: 'https://www.youtube.com' }, { name: 'User-Agent', value: 'Browser-UA/9' }]);
  for (const url of [audio, video]) await t.chrome.webRequest.onHeadersReceived.emit({ tabId: 7, url, statusCode: 206 });

  const { items } = await t.controller.listMedia();
  const item = items.find((i) => i.kind === 'tracks');
  assert.equal(item.title, 'My Holiday Video');
  assert.equal(item.quality, '1080p + 128 kbit/s sound');
  assert.equal(item.maxHeight, 1080);
  assert.equal(item.duration, 212.5);

  const result = await t.controller.sendMedia(item.url);
  assert.equal(result.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.source, 'tracks');
  assert.equal(capture.fileName, 'My Holiday Video.mp4');
  assert.equal(capture.size, 48000000);
  assert.equal(capture.audioSize, 3400000);
  assert.equal(capture.duration, 212.5);
  assert.ok(!capture.url.includes('range=') && !capture.audioUrl.includes('range='));
  assert.equal(capture.referrer, 'https://www.youtube.com/watch?v=abc');
  assert.equal(capture.userAgent, 'Browser-UA/9');                    // the headers recorded for the request the player made
  assert.deepEqual(capture.headers, { Origin: 'https://www.youtube.com' });
});

test('a live or protected YouTube stream is not sent', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit({ tabId: 7, url: `${base}?itag=137&mime=video%2Fmp4&sq=3`, statusCode: 206 });
  const item = (await t.controller.listMedia()).items.find((i) => i.kind === 'tracks');
  assert.equal(item.live, true);
  assert.deepEqual(await t.controller.sendMedia(item.url), { accepted: false, reason: 'live-not-supported' });
  assert.equal(t.app.captures.length, 0);
});

test('a YouTube video page is recognised, other pages are not', async () => {
  const { isVideoPage } = await import('../lib/youtube.js');
  for (const yes of ['https://www.youtube.com/watch?v=abc', 'https://youtu.be/abc', 'https://m.youtube.com/shorts/abc', 'https://www.youtube.com/live/abc', 'https://music.youtube.com/watch?v=abc&list=x']) {
    assert.equal(isVideoPage(yes), true, yes);
  }
  for (const no of ['https://www.youtube.com/', 'https://www.youtube.com/feed/subscriptions', 'https://www.youtube.com/watch', 'https://evil.example/watch?v=abc', 'https://youtube.com.evil.example/watch?v=a', 'http://www.youtube.com/watch?v=abc', 'not a url']) {
    assert.equal(isVideoPage(no), false, no);
  }
});

test('a YouTube tab lists its page; "Download" sends the page address without any cookies', async () => {
  const t = setup();
  t.chrome.cookies.getAll = async () => [{ name: 'SID', value: 'secret' }];
  const { items } = await t.controller.listMedia();
  const page = items.find((i) => i.kind === 'page');
  assert.equal(page.url, 'https://www.youtube.com/watch?v=abc');
  assert.equal(page.title, 'My Holiday Video');

  const result = await t.controller.sendMedia(page.url);
  assert.equal(result.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.url, 'https://www.youtube.com/watch?v=abc');
  assert.equal(capture.source, 'page');
  assert.equal(capture.fileName, 'My Holiday Video');
  assert.equal(capture.cookies, undefined);                        // the sign-in stays in the browser
  assert.equal((await t.controller.sendMedia('https://evil.example/x')).accepted, false);
});
