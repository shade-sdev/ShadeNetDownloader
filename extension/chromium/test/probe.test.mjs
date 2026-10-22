// Looking whether the app is running without starting it (the toolbar popup does this when it opens).
import test from 'node:test';
import assert from 'node:assert/strict';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup(configure) {
  const app = new FakeApp();
  configure?.(app);
  const { chrome } = fakeChrome(app);
  const link = new NativeLink(chrome, {}, manualTimers());
  return { app, chrome, link };
}

test('probe: a running app is found and the link stays connected', async () => {
  const { app, link } = setup();
  assert.equal(await link.probe(1000), true);
  assert.equal(link.status().state, 'connected');
  assert.equal(app.ports[0].sent[0].launch, false);   // asked not to start anything
});

test('probe: a closed app is reported as "not running", not as an error, and is not started', async () => {
  const { app, link } = setup((a) => { a.running = false; });
  assert.equal(await link.probe(1000), false);
  const status = link.status();
  assert.equal(status.state, 'not-running');
  assert.equal(status.lastError, '');
  assert.equal(app.launches, 0);
  assert.equal(link.port, null);
});

test('a real request after a look-only probe starts the app', async () => {
  const { app, link } = setup((a) => { a.running = false; });
  await link.probe(1000);
  assert.equal(await link.ensureConnected(1000), true);
  assert.equal(app.launches, 1);
  assert.equal(app.ports[1].sent[0].launch, true);
  assert.equal(link.status().state, 'connected');
});

test('a request that comes while a probe is still waiting is not lost with it', async () => {
  const { app, link } = setup((a) => { a.running = false; a.answerHello = false; });
  const looking = link.probe(5000);
  await Promise.resolve();
  app.answerHello = true;
  const starting = link.ensureConnected(1000);
  assert.equal(await looking, false);      // the look-only port was given up
  assert.equal(await starting, true);
  assert.equal(app.launches, 1);
});

test('a missing host is an error with its reason, not "not running"', async () => {
  const app = new FakeApp();
  const { chrome } = fakeChrome(app);
  chrome.runtime.connectNative = () => { throw new Error('Specified native messaging host not found.'); };
  const link = new NativeLink(chrome, {}, manualTimers());
  assert.equal(await link.probe(1000), false);
  assert.equal(link.status().state, 'error');
  assert.match(link.status().lastError, /not found/);
});
