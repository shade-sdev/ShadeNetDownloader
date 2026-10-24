// What a page's own "Download" button (content.js) and the toolbar button need from the controller: the name of each video,
// plain video files as well as streams, and a way to look at the app without starting it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Controller, shownItems, tooltipFor } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup({ configureApp, tab } = {}) {
  const app = new FakeApp();
  configureApp?.(app);
  const fake = fakeChrome(app);
  if (tab) Object.assign(fake.chrome.tabs.active, tab);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers });
  controller.registerListeners();
  return { app, controller, link, timers, ...fake };
}

const sender = (t, frameId = 0) => ({ tab: t.chrome.tabs.active, frameId });
const clip = { title: 'Holiday in Lisbon', src: 'https://cdn.example/media/clip.mp4', duration: 95, width: 1280, height: 720 };
const settle = () => new Promise((r) => setTimeout(r, 5));

test('a page that plays a video file gets a button that names the video', async () => {
  const t = setup();
  const answer = await t.controller.onPageVideos(sender(t), [clip]);
  assert.equal(answer.overlay, true);
  assert.equal(answer.items.length, 1);
  assert.equal(answer.items[0].kind, 'file');
  assert.equal(answer.items[0].title, 'Holiday in Lisbon');
  assert.equal(answer.items[0].url, clip.src);
  assert.equal(answer.items[0].duration, 95);
});

test('the toolbar button counts the videos and names them in its tooltip', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t), [clip]);
  await settle();
  assert.ok(t.calls.badge.includes('1'));
  const tip = t.calls.titles.at(-1);
  assert.match(tip, /1 video can be downloaded/);
  assert.match(tip, /Holiday in Lisbon/);
  assert.deepEqual(t.calls.nudges.at(-1), [7, 'media-changed']);   // the page is told to look again
});

test('reporting the same videos again changes nothing and does not start a loop of nudges', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t), [clip]);
  await settle();
  const nudges = t.calls.nudges.length;
  await t.controller.onPageVideos(sender(t), [clip]);
  await settle();
  assert.equal(t.calls.nudges.length, nudges);
});

test('a video file is named after the video when sent: with the page as referrer', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t), [clip]);
  const result = await t.controller.sendMedia(clip.src, t.chrome.tabs.active);
  assert.equal(result.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.url, clip.src);
  assert.equal(capture.fileName, 'Holiday in Lisbon.mp4');
  assert.equal(capture.source, 'video');
  assert.equal(capture.referrer, 'https://site.example/watch/1');
});

test('a file that no frame reported is refused; blob, playlist and non-web sources are never offered', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t), [
    { title: 'a', src: 'blob:https://site.example/1234' },
    { title: 'b', src: 'https://cdn.example/live/master.m3u8' },
    { title: 'c', src: 'file:///C:/x.mp4' },
    { title: 'd', src: '' },
  ]);
  const { items } = await t.controller.listMedia();
  assert.equal(items.length, 0);
  assert.equal((await t.controller.sendMedia('https://cdn.example/other.mp4', t.chrome.tabs.active)).accepted, false);
});

test('videos in a frame of the page count too, and a new page forgets the old ones', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t, 3), [clip]);
  assert.equal((await t.controller.listMedia()).items.length, 1);
  await t.chrome.tabs.onUpdated.emit(7, { status: 'loading' });
  assert.equal((await t.controller.listMedia()).items.length, 0);
});

test('on a YouTube page the picture and sound it plays are not counted as a second video', () => {
  const items = [
    { kind: 'tracks', title: 'T', protected: false },
    { kind: 'page', title: 'T', protected: false },
  ];
  assert.deepEqual(shownItems(items).map((i) => i.kind), ['page']);
  assert.equal(shownItems([{ kind: 'tracks', title: 'T', protected: true }]).length, 0);   // protected: nothing to offer
});

test('the name comes from the page, not the tab, once the page has told it', async () => {
  const t = setup({ tab: { title: '(3) Some site - Home', url: 'https://site.example/post/5' } });
  assert.equal(t.controller.pageTitle(t.chrome.tabs.active), 'Some site - Home');
  await t.controller.onPageVideos(sender(t), [{ ...clip, title: '  The   real: name?  ' }]);
  assert.equal(t.controller.pageTitle(t.chrome.tabs.active), 'The real name');
});

test('tooltip: names are listed, long lists are cut', () => {
  assert.equal(tooltipFor([]), 'ShadeNet Downloader');
  const many = Array.from({ length: 7 }, (_, i) => ({ title: 'Video ' + i }));
  const tip = tooltipFor(many);
  assert.match(tip, /7 videos can be downloaded/);
  assert.match(tip, /and 2 more/);
  assert.match(tooltipFor([{ title: 'x'.repeat(200) }]), /…/);
});

test('the page button can be switched off from the popup and the choice is remembered', async () => {
  const t = setup();
  const ask = (m) => new Promise((resolve) => t.controller.onPopupMessage(m, resolve, undefined));
  assert.equal((await ask({ type: 'status' })).overlayEnabled, true);
  assert.equal((await ask({ type: 'set-overlay', value: false })).overlayEnabled, false);
  assert.equal(t.store.overlay, false);
  const answer = await t.controller.onPageVideos(sender(t), [clip]);
  assert.equal(answer.overlay, false);
});

test('a message from a page goes through the same handler as the popup, and carries the tab it came from', async () => {
  const t = setup();
  const send = (m, s) => new Promise((resolve) => t.controller.onPopupMessage(m, resolve, s));
  const answer = await send({ type: 'page-videos', videos: [clip] }, sender(t));
  assert.equal(answer.items[0].title, 'Holiday in Lisbon');
  const sent = await send({ type: 'download-media', url: clip.src }, sender(t));
  assert.equal(sent.accepted, true);
  assert.equal((await send({ type: 'page-videos', videos: [clip] }, undefined)).items.length, 0);   // no tab: ignored
});

test('probing from the popup does not start a closed app; a download does', async () => {
  const t = setup({ configureApp: (a) => { a.running = false; } });
  const ask = (m) => new Promise((resolve) => t.controller.onPopupMessage(m, resolve, undefined));
  const status = await ask({ type: 'probe' });
  assert.equal(status.state, 'not-running');
  assert.equal(status.connected, false);
  assert.equal(t.app.launches, 0);

  const started = await ask({ type: 'connect' });
  assert.equal(started.connected, true);
  assert.equal(t.app.launches, 1);
});

test('a title that ends with a full stop does not make "Title..mp4"', async () => {
  const t = setup();
  await t.controller.onPageVideos(sender(t), [{ ...clip, title: 'How to share with just friends.' }]);
  await t.controller.sendMedia(clip.src, t.chrome.tabs.active);
  assert.equal(t.app.captures[0].fileName, 'How to share with just friends.mp4');
});

test('a video source the browser could not load is not offered: the player tried it and moved on', async () => {
  const t = setup();
  const dead = { title: 'Dead', src: 'https://storage.example/old/1305.mp4#mp4/chunk/5/1305/2097152/720p', duration: 0, failed: true };
  const { items } = await t.controller.onPageVideos(sender(t), [dead, clip]);
  assert.deepEqual(items.map((i) => i.url), [clip.src]);
});

test('a video file the server refused the browser is not offered, and is again once it answers', async () => {
  const t = setup();
  const answer = (statusCode) => t.chrome.webRequest.onHeadersReceived.emit({ tabId: 7, url: clip.src, type: 'media', statusCode, responseHeaders: [{ name: 'Content-Type', value: 'text/xml' }] });
  await answer(403);
  // (the element's address carries a fragment the request never has)
  let { items } = await t.controller.onPageVideos(sender(t), [{ ...clip, src: clip.src + '#t=10' }]);
  assert.equal(items.length, 0);
  assert.match(await t.controller.diagnose(), /could not load/);

  await answer(206);
  ({ items } = await t.controller.onPageVideos(sender(t), [clip]));
  assert.deepEqual(items.map((i) => i.url), [clip.src]);
});

test('a refusal on one tab says nothing about the same address on another', async () => {
  const t = setup();
  await t.chrome.webRequest.onHeadersReceived.emit({ tabId: 99, url: clip.src, type: 'media', statusCode: 403, responseHeaders: [] });
  const { items } = await t.controller.onPageVideos(sender(t), [clip]);
  assert.equal(items.length, 1);
});
