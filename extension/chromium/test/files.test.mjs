// A player that reads one whole video file itself, a range at a time, under any name and type: the file is offered when its
// first bytes are a video file, and not otherwise.
import test from 'node:test';
import assert from 'node:assert/strict';
import { RangedFileTracker, fileFormat, looksLikeRangedFile, wholeSize } from '../lib/files.js';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';
import * as real from './fixtures.mjs';

const MB = 1024 * 1024;
const part = (tabId, url, { total = 700 * MB, type = 'application/octet-stream', status = 206, kind = 'xmlhttprequest' } = {}) => ({
  tabId, url, type: kind, statusCode: status, documentUrl: 'https://player.example/e/1',
  responseHeaders: [{ name: 'Content-Type', value: type }, { name: 'Content-Range', value: `bytes 0-${2 * MB - 1}/${total}` }, { name: 'Content-Length', value: String(2 * MB) }],
});
const ascii = (s) => [...s].map((c) => c.charCodeAt(0));
const WEBM = new Uint8Array([0x1a, 0x45, 0xdf, 0xa3, 0x9f, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x84, ...ascii('webm'), 0, 0]);
const MKV = new Uint8Array([0x1a, 0x45, 0xdf, 0xa3, 0xa3, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x88, ...ascii('matroska'), 0, 0]);
const FLV = new Uint8Array([...ascii('FLV'), 1, 5, 0, 0, 0, 9, 0, 0, 0, 0]);
const NOISE = new Uint8Array(4096).map((_, i) => (i * 7919 + 13) % 251);
const settle = () => new Promise((r) => setTimeout(r, 5));

test('the whole size is read from Content-Range', () => {
  assert.equal(wholeSize(part(1, 'https://x.example/a')), 700 * MB);
  assert.equal(wholeSize({ responseHeaders: [{ name: 'content-range', value: 'bytes 0-1/*' }] }), 0);
  assert.equal(wholeSize({}), 0);
});

test('only a partial answer of a big file, typed as binary or video, is worth a look', () => {
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/c/89a7.1582.4'), 'application/octet-stream'), true);
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v'), 'video/mp4'), true);
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v', { status: 200 }), 'video/mp4'), false);     // a whole answer is a piece, not a range
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v', { total: MB }), 'video/mp4'), false);      // too small to be a video
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v', { kind: 'media' }), 'video/mp4'), false);  // the <video> itself: reported by the page
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v'), 'text/html'), false);
  assert.equal(looksLikeRangedFile(part(1, 'https://x.example/v'), 'image/jpeg'), false);
});

test('the first bytes say which video file it is, or that it is none', () => {
  assert.equal(fileFormat(real.fmp4Both), 'mp4');
  assert.equal(fileFormat(WEBM), 'webm');
  assert.equal(fileFormat(MKV), 'mkv');
  assert.equal(fileFormat(FLV), 'flv');
  assert.equal(fileFormat(real.tsBoth), 'ts');
  assert.equal(fileFormat(NOISE), null);            // bytes that are no video file as they are
  assert.equal(fileFormat(real.mp3), null);
  assert.equal(fileFormat(new Uint8Array(4)), null);
});

function tracker(heads) {
  const asked = [];
  const t = new RangedFileTracker({ fetchHead: async (url) => { asked.push(url); if (!(url in heads)) throw new Error('403'); return heads[url]; } });
  return { t, asked };
}

test('a video file read in ranges is offered once, with its size and format', async () => {
  const url = 'https://media.example/c/f/89a7f.1582877106.4';
  const { t, asked } = tracker({ [url]: real.fmp4Both });
  t.consider(part(7, url));
  t.consider(part(7, url));   // the next range of the same file
  await settle();
  assert.deepEqual(asked, [url]);
  const [item] = t.items(7);
  assert.equal(item.url, url);
  assert.equal(item.size, 700 * MB);
  assert.equal(item.format, 'mp4');
  assert.equal(item.kind, 'both');
  assert.equal(item.protected, false);
});

test('bytes that are no video file, or that cannot be read, are not offered, and diagnosis says why', async () => {
  const noise = 'https://media.example/blob/1';
  const gone = 'https://media.example/blob/2';
  const { t } = tracker({ [noise]: NOISE });
  t.consider(part(7, noise));
  t.consider(part(7, gone));
  await settle();
  assert.deepEqual(t.items(7), []);
  assert.deepEqual(t.describe(7).map((f) => f.status), ['not-a-video-file', 'unreadable']);
});

test('a tab that plays a playlist leaves its ranges to it', async () => {
  const url = 'https://media.example/v.mp4';
  const t = new RangedFileTracker({ fetchHead: async () => real.fmp4Both, skip: () => true });
  t.consider(part(7, url));
  await settle();
  assert.deepEqual(t.items(7), []);
});

function setup(heads) {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({
    chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers,
    fetchHead: async (url) => { if (!(url in heads)) throw new Error('403'); return heads[url]; },
  });
  controller.registerListeners();
  return { app, controller, ...fake };
}

test('the file the player reads is offered before a <video> address its code answers itself, and is sent with its size', async () => {
  const url = 'https://media.example/c/f/89a7f.1582877106.4';
  const front = 'https://storage.example/old/1582877106.mp4';
  const t = setup({ [url]: real.fmp4Both });
  const sender = { tab: t.chrome.tabs.active, frameId: 4, url: 'https://player.example/e/1' };
  await t.controller.onPageVideos(sender, [{ title: 'Film 1080p.mkv', src: front, duration: 8209, box: [760, 360], opacity: 1 }]);
  await t.chrome.webRequest.onHeadersReceived.emit(part(7, url));
  await settle();

  const { items } = await t.controller.listMedia();
  assert.deepEqual(items.map((i) => i.url), [url, front]);
  assert.equal(items[0].duration, 8209);   // the length the player reports

  const result = await t.controller.sendMedia(url, t.chrome.tabs.active);
  assert.equal(result.accepted, true);
  const capture = t.app.captures.at(-1);
  assert.equal(capture.url, url);
  assert.equal(capture.size, 700 * MB);
  assert.equal(capture.source, 'video');
  assert.equal(capture.fileName, 'Film 1080p.mp4');   // the video's own name, without the ".mkv" it carried
  assert.equal(capture.referrer, 'https://player.example/e/1');   // the player's page, not the outer page
  assert.match(await t.controller.diagnose(), /"status": "ok"/);
});

test('a file read in ranges whose bytes are no video file is never offered', async () => {
  const url = 'https://media.example/c/f/89a7f.1582877106.4';
  const t = setup({ [url]: NOISE });
  await t.chrome.webRequest.onHeadersReceived.emit(part(7, url));
  await settle();
  const { items } = await t.controller.listMedia();
  assert.equal(items.length, 0);
  assert.match(await t.controller.diagnose(), /not-a-video-file/);
});
