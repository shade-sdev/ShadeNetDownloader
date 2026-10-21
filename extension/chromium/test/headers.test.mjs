import test from 'node:test';
import assert from 'node:assert/strict';
import { HeaderRecorder } from '../lib/headers.js';
import { FakeApp, fakeChrome } from './fakes.mjs';

test('it listens for request headers including the ones that need extraHeaders (Cookie)', () => {
  const { chrome } = fakeChrome(new FakeApp());
  let registered;
  chrome.webRequest.onBeforeSendHeaders.addListener = (fn, filter, extra) => { registered = { fn, filter, extra }; };
  new HeaderRecorder(chrome);
  assert.deepEqual(registered.filter, { urls: ['<all_urls>'] });
  assert.deepEqual(registered.extra, ['requestHeaders', 'extraHeaders']);
});

test('headers are remembered per address', () => {
  const { chrome } = fakeChrome(new FakeApp());
  const recorder = new HeaderRecorder(chrome);
  chrome.webRequest.onBeforeSendHeaders.listeners[0]({ url: 'https://a/x.zip', requestHeaders: [{ name: 'Cookie', value: 'a=1' }] });
  assert.deepEqual(recorder.lookup('https://a/x.zip'), [{ name: 'Cookie', value: 'a=1' }]);
  assert.equal(recorder.lookup('https://a/other'), undefined);
});

test('a newer request to the same address replaces the older one', () => {
  const recorder = new HeaderRecorder(null);
  recorder.record('https://a/x', [{ name: 'Cookie', value: 'old' }]);
  recorder.record('https://a/x', [{ name: 'Cookie', value: 'new' }]);
  assert.equal(recorder.lookup('https://a/x')[0].value, 'new');
});

test('entries expire after two minutes', () => {
  let now = 1000;
  const recorder = new HeaderRecorder(null, () => now);
  recorder.record('https://a/x', [{ name: 'Cookie', value: 'a=1' }]);
  now += 119_000;
  assert.ok(recorder.lookup('https://a/x'));
  now += 2_000;
  assert.equal(recorder.lookup('https://a/x'), undefined);
});

test('memory is bounded: the oldest entries go first', () => {
  const recorder = new HeaderRecorder(null);
  for (let i = 0; i < 250; i++) recorder.record(`https://a/${i}`, [{ name: 'Cookie', value: String(i) }]);
  assert.equal(recorder.lookup('https://a/0'), undefined);
  assert.equal(recorder.lookup('https://a/49'), undefined);
  assert.ok(recorder.lookup('https://a/50'));
  assert.ok(recorder.lookup('https://a/249'));
  assert.equal(recorder.byUrl.size, 200);
});

test('malformed records are ignored', () => {
  const recorder = new HeaderRecorder(null);
  recorder.record('', [{ name: 'a', value: 'b' }]);
  recorder.record('https://a/x', undefined);
  assert.equal(recorder.byUrl.size, 0);
});
