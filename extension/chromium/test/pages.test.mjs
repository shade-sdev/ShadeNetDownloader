// Videos whose picture comes through a buffer (Facebook, Instagram ...: a blob: address, nothing on the network to name the
// file) are downloaded by giving their PAGE to yt-dlp; so are YouTube players embedded in other sites' pages (iframes).
import test from 'node:test';
import assert from 'node:assert/strict';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { acceptPageAddress, embedWatchUrl, isDecoyVideo, isPageSite, looksLikeOneVideo } from '../lib/sites.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup(tab) {
  const app = new FakeApp();
  const fake = fakeChrome(app);
  Object.assign(fake.chrome.tabs.active, tab);
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers });
  controller.registerListeners();
  return { app, controller, ...fake };
}

const from = (t, url, frameId = 0) => ({ tab: t.chrome.tabs.active, frameId, url });
const blobVideo = (extra = {}) => ({ title: 'How to Share', src: 'blob:https://www.facebook.com/abc', blob: true, drm: false, permalink: '', duration: 74, width: 640, height: 360, ...extra });
const FB = 'https://www.facebook.com/facebook/videos/10153231379946729/';

test('a Facebook video page whose player uses a buffer gets a button that sends the page', async () => {
  const t = setup({ title: 'How to Share With Just Friends | Facebook', url: FB });
  const answer = await t.controller.onPageVideos(from(t, FB), [blobVideo()]);
  assert.equal(answer.items.length, 1);
  assert.equal(answer.items[0].kind, 'page');
  assert.equal(answer.items[0].url, FB);
  assert.equal(answer.items[0].title, 'How to Share');

  const sent = await t.controller.sendMedia(FB, t.chrome.tabs.active);
  assert.equal(sent.accepted, true);
  const capture = t.app.captures[0];
  assert.equal(capture.url, FB);
  assert.equal(capture.source, 'page');
  assert.equal(capture.fileName, 'How to Share');
  assert.equal(capture.cookies, undefined);   // the browser's sign-in stays in the browser
});

test('in a feed the address of the video\'s own post is used, not the feed\'s', async () => {
  const t = setup({ title: 'Facebook', url: 'https://www.facebook.com/' });
  const answer = await t.controller.onPageVideos(from(t, 'https://www.facebook.com/'), [blobVideo({ permalink: 'https://www.facebook.com/reel/1234567#comments' })]);
  assert.equal(answer.items[0].url, 'https://www.facebook.com/reel/1234567');   // no fragment
});

test('a feed with no address for the video offers nothing (yt-dlp cannot read a front page)', async () => {
  const t = setup({ title: 'Facebook', url: 'https://www.facebook.com/' });
  assert.equal((await t.controller.onPageVideos(from(t, 'https://www.facebook.com/'), [blobVideo()])).items.length, 0);
});

test('an address the page reports on some other site is not accepted', async () => {
  const t = setup({ title: 'Facebook', url: 'https://www.facebook.com/' });
  const answer = await t.controller.onPageVideos(from(t, 'https://www.facebook.com/'), [blobVideo({ permalink: 'https://evil.example/reel/1' })]);
  assert.equal(answer.items.length, 0);
});

test('a protected (DRM) player is left alone, whatever the site', async () => {
  const t = setup({ title: 'Facebook', url: FB });
  assert.equal((await t.controller.onPageVideos(from(t, FB), [blobVideo({ drm: true })])).items.length, 0);

  const other = setup({ title: 'Some site', url: 'https://some-site.example/watch/12' });
  assert.equal((await other.controller.onPageVideos(from(other, 'https://some-site.example/watch/12'), [blobVideo({ drm: true })])).items.length, 0);
});

test('a hidden video on a site yt-dlp is not known for is offered as a try, with the frame\'s own page', async () => {
  const frame = 'https://player.example/embed/42?autoplay=1';
  const t = setup({ title: 'A blog', url: 'https://blog.example/post/1' });
  const answer = await t.controller.onPageVideos(from(t, frame, 5), [blobVideo({ src: 'blob:https://player.example/1', title: 'Open film' })]);
  assert.equal(answer.items.length, 1);
  assert.equal(answer.items[0].kind, 'page');
  assert.equal(answer.items[0].guess, true);          // the button says it is a try
  assert.equal(answer.items[0].url, frame);           // the address the BROWSER reports for the frame, not what the page claims
  assert.equal((await t.controller.sendMedia(frame, t.chrome.tabs.active)).accepted, true);
  assert.equal(t.app.captures[0].source, 'page');

  // a page cannot send ShadeNet somewhere else by claiming another address
  const other = setup({ title: 'A blog', url: 'https://blog.example/post/1' });
  const forged = await other.controller.onPageVideos(from(other, frame), [blobVideo({ src: 'blob:https://player.example/1', permalink: 'https://evil.example/x' })]);
  assert.equal(forged.items[0].url, frame);
});

test('a stream the page was seen fetching wins over offering the page', async () => {
  const t = setup({ title: 'x', url: FB });
  t.controller.media.add(7, 'https://cdn.example/x/master.m3u8', { kind: 'master', children: [], maxHeight: 720, live: false, duration: 60 });
  const answer = await t.controller.onPageVideos(from(t, FB), [blobVideo()]);
  assert.deepEqual(answer.items.map((i) => i.kind), ['master']);
});

test('a YouTube player embedded in another site is offered as its watch page', async () => {
  const t = setup({ title: 'My blog', url: 'https://blog.example/post/1' });
  const frame = 'https://www.youtube.com/embed/aqz-KE-bpKQ?rel=0';
  const answer = await t.controller.onPageVideos(from(t, frame, 4), [{ title: 'Big Buck Bunny', src: 'blob:https://www.youtube.com/z', blob: true, drm: false, permalink: '' }]);
  assert.equal(answer.items.length, 1);
  assert.equal(answer.items[0].url, 'https://www.youtube.com/watch?v=aqz-KE-bpKQ');
  assert.equal(answer.items[0].title, 'Big Buck Bunny');
  assert.equal((await t.controller.sendMedia(answer.items[0].url, t.chrome.tabs.active)).accepted, true);
  assert.equal(t.app.captures[0].source, 'page');
});

test('the site list, one-video addresses and embeds', () => {
  assert.ok(isPageSite('https://www.facebook.com/x'));
  assert.ok(isPageSite('https://m.facebook.com/x'));
  assert.ok(isPageSite('https://x.com/a/status/1'));
  assert.ok(!isPageSite('https://notfacebook.com/x'));
  assert.ok(!isPageSite('https://facebook.com.evil.example/x'));
  assert.ok(!isPageSite('javascript:alert(1)'));
  assert.ok(isPageSite('http://127.0.0.1:9/x', ['127.0.0.1']));

  assert.ok(looksLikeOneVideo(FB));
  assert.ok(looksLikeOneVideo('https://www.facebook.com/watch/?v=123'));
  assert.ok(looksLikeOneVideo('https://www.facebook.com/reel/123'));
  assert.ok(looksLikeOneVideo('https://x.com/user/status/123'));
  assert.ok(!looksLikeOneVideo('https://www.facebook.com/'));
  assert.ok(!looksLikeOneVideo('https://www.facebook.com/watch/'));
  assert.ok(!looksLikeOneVideo('https://www.facebook.com/someone/videos/'));

  assert.equal(embedWatchUrl('https://www.youtube-nocookie.com/embed/abcdefghijk?x=1'), 'https://www.youtube.com/watch?v=abcdefghijk');
  assert.equal(embedWatchUrl('https://evil.example/embed/abcdefghijk'), null);
  assert.equal(acceptPageAddress('https://www.facebook.com/reel/1#x'), 'https://www.facebook.com/reel/1');
  assert.equal(acceptPageAddress('https://evil.example/reel/1'), '');
});

// ---- a hidden decoy <video> next to the real one, whose picture comes through a buffer (any site that does this) ----

const TIKTOK = 'https://www.tiktok.com/@scout2015/video/6718335390845095173';

test('a tiny see-through <video> is not offered, so the real video\'s page is', async () => {
  const t = setup({ title: 'Scout | TikTok', url: TIKTOK });
  const decoy = { title: 'TikTok', src: 'https://cdn.example/webapp/playback1.mp4', blob: false, drm: false, permalink: '', duration: 0, width: 0, height: 0, box: [50, 50], opacity: 0.001 };
  const real = blobVideo({ title: 'Scramble up ur name', src: 'blob:https://www.tiktok.com/358b0f8f', permalink: TIKTOK, box: [432, 768], opacity: 0 });

  const answer = await t.controller.onPageVideos(from(t, TIKTOK), [decoy, real]);

  assert.deepEqual(answer.items.map((i) => [i.kind, i.url]), [['page', TIKTOK]]);
});

test('what makes a video a decoy is its size and see-through-ness, never its address', () => {
  assert.equal(isDecoyVideo({ box: [50, 50], opacity: 0.001 }), true);
  assert.equal(isDecoyVideo({ box: [1, 1], opacity: 0 }), true);
  assert.equal(isDecoyVideo({ box: [432, 768], opacity: 0 }), false);     // a site that plays its video see-through
  assert.equal(isDecoyVideo({ box: [50, 50], opacity: 1 }), false);       // a small video that can be seen
  assert.equal(isDecoyVideo({ src: 'https://x.example/v.mp4' }), false);  // an older page script that reports no size
  assert.equal(isDecoyVideo(null), false);
});

// ---- live streams: a player that feeds a buffer and never ends (TikTok, Instagram, Facebook, X ... any site yt-dlp reads) ----

const liveVideo = (extra = {}) => blobVideo({ title: 'Going live', src: 'blob:https://www.tiktok.com/live-1', blob: true, live: true, duration: 0, ...extra });

test('a live page of a known site gets a button that records it, whatever its address looks like', async () => {
  for (const page of ['https://www.tiktok.com/@scout2015/live', 'https://www.instagram.com/someone/live/', 'https://www.facebook.com/someone/live', 'https://x.com/i/broadcasts/1yNGaQZEoQBGj']) {
    const t = setup({ title: 'Live', url: page });
    const answer = await t.controller.onPageVideos(from(t, page), [liveVideo()]);
    assert.deepEqual(answer.items.map((i) => [i.kind, i.url, i.live]), [['page', page, true]], page);
  }
});

test('a live page that is not on a site yt-dlp is known for gets no button', async () => {
  const t = setup({ title: 'Live', url: 'https://unknown.example/someone/live' });
  const answer = await t.controller.onPageVideos(from(t, 'https://unknown.example/someone/live'), [liveVideo()]);
  assert.equal(answer.items.filter((i) => i.kind === 'page' && !i.guess).length, 0);
});

test('a feed video that is not live still needs its own address (the front page is not a video)', async () => {
  const t = setup({ title: 'TikTok', url: 'https://www.tiktok.com/' });
  const answer = await t.controller.onPageVideos(from(t, 'https://www.tiktok.com/'), [liveVideo({ live: false })]);
  assert.equal(answer.items.length, 0);
});

test('live addresses look like one video; the bare /live front door of a site does not', () => {
  assert.equal(looksLikeOneVideo('https://www.tiktok.com/@scout2015/live'), true);
  assert.equal(looksLikeOneVideo('https://www.instagram.com/someone/live/'), true);
  assert.equal(looksLikeOneVideo('https://www.facebook.com/live/producer/12345'), true);
  assert.equal(looksLikeOneVideo('https://x.com/i/broadcasts/1yNGaQZEoQBGj'), true);
  assert.equal(looksLikeOneVideo('https://www.tiktok.com/live'), false);
  assert.equal(looksLikeOneVideo('https://www.twitch.tv/live/'), false);
});
