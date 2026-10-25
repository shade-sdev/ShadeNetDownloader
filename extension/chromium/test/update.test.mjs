// The extension is loaded from the app's folder, which an app update replaces: when the app says it has a new version, the
// extension reloads itself once, so the browser runs the new files without a restart.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Controller } from '../lib/controller.js';
import { HeaderRecorder } from '../lib/headers.js';
import { NativeLink } from '../lib/native.js';
import { FakeApp, fakeChrome, manualTimers } from './fakes.mjs';

function setup(storage = {}) {
  const app = new FakeApp();
  const fake = fakeChrome(app, { storage });
  let reloads = 0;
  fake.chrome.runtime.reload = () => { reloads++; };
  const timers = manualTimers();
  let controller;
  const link = new NativeLink(fake.chrome, { onSettings: (s) => controller.applyAppSettings(s) }, timers);
  controller = new Controller({ chrome: fake.chrome, link, recorder: new HeaderRecorder(fake.chrome), userAgent: 'Test-UA/1', timers });
  return { controller, link, timers, chrome: fake.chrome, reloads: () => reloads };
}

const settle = () => new Promise((r) => setTimeout(r, 5));
const stored = async (t) => (await t.chrome.storage.local.get({ appVersion: '' })).appVersion;

test('the first app the extension meets is remembered, and nothing is reloaded', async () => {
  const t = setup();
  t.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();
  assert.equal(await stored(t), '1.0.3');
  assert.equal(t.timers.count(), 0);
  assert.equal(t.reloads(), 0);
});

test('the same app version again changes nothing', async () => {
  const t = setup({ appVersion: '1.0.3' });
  t.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();
  assert.equal(t.timers.count(), 0);
  assert.equal(t.reloads(), 0);
});

test('an updated app makes the extension reload once, after a quiet moment, storing the new version just before', async () => {
  const t = setup({ appVersion: '1.0.2' });
  t.controller.applyAppSettings({ appVersion: '1.0.3' });
  t.controller.applyAppSettings({ appVersion: '1.0.3' });   // (settings may come twice: hello-ack, then settings)
  await settle();
  assert.equal(await stored(t), '1.0.2', 'not yet: a worker stopped before the reload must try again next time');
  assert.equal(t.reloads(), 0);

  t.timers.fire();
  await settle();
  assert.equal(t.reloads(), 0);   // one quiet check is not enough
  t.timers.fire();
  await settle();
  assert.equal(t.reloads(), 1);
  assert.equal(await stored(t), '1.0.3');
  assert.equal(t.timers.count(), 0);
});

test('a worker stopped before its reload reloads when it next meets the updated app', async () => {
  const first = setup({ appVersion: '1.0.2' });
  first.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();   // ... and the browser stops the worker here, before its timers run

  const second = setup({ appVersion: await stored(first) });   // what the first left stored, a fresh worker
  second.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();
  second.timers.fire();
  await settle();
  second.timers.fire();
  await settle();
  assert.equal(second.reloads(), 1);
});

test('a hand-over waiting for the app is let finish before the reload', async () => {
  const t = setup({ appVersion: '1.0.2' });
  t.link.pending.set('x', {});
  t.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();
  for (let i = 0; i < 5; i++) {
    t.timers.fire();
    await settle();
  }
  assert.equal(t.reloads(), 0);

  t.link.pending.delete('x');
  t.timers.fire();
  await settle();
  t.timers.fire();
  await settle();
  assert.equal(t.reloads(), 1);
});

test('when nothing can be remembered, the extension never reloads (it could otherwise do so on every connection)', async () => {
  const t = setup({ appVersion: '1.0.2' });
  t.chrome.storage.local.set = async () => { throw new Error('quota'); };
  t.controller.applyAppSettings({ appVersion: '1.0.3' });
  await settle();
  t.timers.fire();
  t.timers.fire();
  assert.equal(t.reloads(), 0);
});
