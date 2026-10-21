import test from 'node:test';
import assert from 'node:assert/strict';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup(configure) {
  const app = new FakeApp();
  configure?.(app);
  const { chrome } = fakeChrome(app);
  const timers = manualTimers();
  const settings = [];
  const link = new NativeLink(chrome, { onSettings: (s) => settings.push(s) }, timers);
  return { app, chrome, timers, settings, link };
}

test('the first connection says hello and is connected once the app answers', async () => {
  const { app, link, settings } = setup();
  assert.equal(link.connected, false);
  assert.equal(await link.ensureConnected(1000), true);
  assert.equal(link.connected, true);
  assert.equal(app.ports[0].sent[0].type, 'hello');
  assert.equal(app.ports[0].sent[0].extensionVersion, '1.0.0');
  assert.equal(settings[0].appVersion, '9.9');
  assert.equal(link.status().appVersion, '9.9');
});

test('a connected link is reused, not reopened', async () => {
  const { app, link } = setup();
  await link.ensureConnected(1000);
  await link.ensureConnected(1000);
  assert.equal(app.ports.length, 1);
});

test('an app that stays silent makes ensureConnected fail after the timeout', async () => {
  const { link, timers } = setup((app) => { app.answerHello = false; });
  const pending = link.ensureConnected(5000);
  await Promise.resolve();
  timers.fire((t) => t.ms === 5000);
  assert.equal(await pending, false);
  assert.match(link.lastError, /did not answer/);
  assert.equal(link.connected, false);
});

test('a host error is remembered and the failed connection is not "connected"', async () => {
  const { link } = setup((app) => { app.available = false; });
  assert.equal(await link.ensureConnected(1000), false);
  assert.match(link.lastError, /not running/);
  assert.equal(link.port, null);
});

test('a capture request resolves with the matching result', async () => {
  const { link, app } = setup();
  await link.ensureConnected(1000);
  app.capture = (m) => ({ accepted: true, reason: 'added', jobId: 'job-' + m.id });
  const result = await link.request({ type: 'capture', id: 'abc', url: 'https://x/y.zip' });
  assert.deepEqual(result, { type: 'capture-result', id: 'abc', accepted: true, reason: 'added', jobId: 'job-abc' });
});

test('concurrent requests are matched by id, whatever order the app answers in', async () => {
  const { link, app } = setup();
  await link.ensureConnected(1000);
  const resolvers = [];
  app.capture = (m) => new Promise((resolve) => resolvers.push(() => resolve({ accepted: true, reason: m.id })));
  const a = link.request({ type: 'capture', id: 'a' });
  const b = link.request({ type: 'capture', id: 'b' });
  await new Promise((r) => setTimeout(r, 10));
  resolvers[1]();
  resolvers[0]();
  assert.equal((await a).reason, 'a');
  assert.equal((await b).reason, 'b');
});

test('a request times out when the app never replies', async () => {
  const { link, app, timers } = setup();
  await link.ensureConnected(1000);
  app.capture = () => null;
  const pending = link.request({ type: 'capture', id: 'slow' }, 3000);
  await new Promise((r) => setTimeout(r, 5));
  timers.fire((t) => t.ms === 3000);
  await assert.rejects(pending, /did not answer/);
});

test('losing the connection rejects what is in flight and allows reconnecting', async () => {
  const { link, app } = setup();
  await link.ensureConnected(1000);
  app.capture = () => null;
  const pending = link.request({ type: 'capture', id: 'x' });
  await new Promise((r) => setTimeout(r, 5));
  await app.ports[0].close();
  await assert.rejects(pending, /closed/);
  assert.equal(link.connected, false);

  assert.equal(await link.ensureConnected(1000), true);
  assert.equal(app.ports.length, 2);
});

test('requesting without a connection fails immediately', async () => {
  const { link } = setup();
  await assert.rejects(link.request({ type: 'capture', id: 'x' }), /Not connected/);
});

test('settings pushed by the app reach the handler; junk is ignored', async () => {
  const { link, app, settings } = setup();
  await link.ensureConnected(1000);
  await app.ports[0].deliver({ type: 'settings', enabled: false, excludedHosts: ['a.com'] });
  await app.ports[0].deliver(null);
  await app.ports[0].deliver({ nope: 1 });
  await app.ports[0].deliver({ type: 'something-new' });
  assert.equal(settings.length, 2);
  assert.equal(settings[1].enabled, false);
  assert.equal(link.connected, true);
});

test('connectNative throwing is reported, not thrown', async () => {
  const app = new FakeApp();
  const { chrome } = fakeChrome(app);
  chrome.runtime.connectNative = () => { throw new Error('Specified native messaging host not found.'); };
  const link = new NativeLink(chrome, {}, manualTimers());
  assert.equal(await link.ensureConnected(1000), false);
  assert.match(link.lastError, /not found/);
});

test('the default timers work in a browser, where setTimeout throws unless called on the global object', async () => {
  const realSet = globalThis.setTimeout;
  const realClear = globalThis.clearTimeout;
  globalThis.setTimeout = function strictSetTimeout(...args) {
    if (this !== globalThis) throw new TypeError('Illegal invocation');
    return realSet(...args);
  };
  globalThis.clearTimeout = function strictClearTimeout(...args) {
    if (this !== globalThis) throw new TypeError('Illegal invocation');
    return realClear(...args);
  };

  try {
    const app = new FakeApp();
    const { chrome } = fakeChrome(app);
    const link = new NativeLink(chrome, {});   // no injected timers: the real defaults
    assert.equal(await link.ensureConnected(1000), true);
    app.capture = () => ({ accepted: true, reason: 'added' });
    assert.equal((await link.request({ type: 'capture', id: 'z' })).accepted, true);
  } finally {
    globalThis.setTimeout = realSet;
    globalThis.clearTimeout = realClear;
  }
});
