import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { HeaderRecorder, initiatorOf, ownOrigin } from '../lib/headers.js';
import { FakeApp, fakeChrome } from './fakes.mjs';

// Firefox runs the same code as Chrome; only its manifest differs (extension/firefox/manifest.json).
const chromium = JSON.parse(readFileSync(new URL('../manifest.json', import.meta.url), 'utf8'));
const firefox = JSON.parse(readFileSync(new URL('../../firefox/manifest.json', import.meta.url), 'utf8'));

test('the Firefox manifest is the Chromium one, but for what Firefox does its own way', () => {
  for (const key of ['manifest_version', 'name', 'description', 'version', 'action', 'icons', 'permissions', 'host_permissions', 'content_scripts']) {
    assert.deepEqual(firefox[key], chromium[key], `"${key}" differs between the two manifests`);
  }
  // a background script (Firefox has no extension service worker), the same file
  assert.deepEqual(firefox.background, { scripts: [chromium.background.service_worker], type: chromium.background.type });
  // a fixed id (what the native host's manifest allows), and nothing Chrome-only
  assert.match(firefox.browser_specific_settings.gecko.id, /^[\w.-]+@[\w.-]+$/);
  assert.equal(firefox.key, undefined);
  assert.equal(firefox.minimum_chrome_version, undefined);
});

/** What Firefox's webRequest does with an option it does not know: it refuses the listener. */
function firefoxLike(chrome) {
  const events = chrome.webRequest.onBeforeSendHeaders;
  const add = events.addListener.bind(events);
  events.addListener = (fn, filter, extra) => {
    if (extra?.includes('extraHeaders')) throw new Error("Type error for parameter extraInfoSpec: Invalid enumeration value \"extraHeaders\"");
    add(fn, filter, extra);
  };
  chrome.runtime.getURL = (path) => `moz-extension://0f3c2b8e-1111-4a2b-9c3d-123456789abc/${path}`;
  return chrome;
}

test('in Firefox the headers are listened for without Chrome\'s extraHeaders', () => {
  const chrome = firefoxLike(fakeChrome(new FakeApp()).chrome);
  const recorder = new HeaderRecorder(chrome);
  assert.equal(chrome.webRequest.onBeforeSendHeaders.listeners.length, 1);
  chrome.webRequest.onBeforeSendHeaders.listeners[0]({ url: 'https://a/x.zip', originUrl: 'https://a/page', requestHeaders: [{ name: 'Cookie', value: 'a=1' }] });
  assert.deepEqual(recorder.lookup('https://a/x.zip'), [{ name: 'Cookie', value: 'a=1' }]);
});

test('the extension\'s own requests are not remembered, in Firefox as in Chrome', () => {
  const chrome = firefoxLike(fakeChrome(new FakeApp()).chrome);
  const recorder = new HeaderRecorder(chrome);
  chrome.webRequest.onBeforeSendHeaders.listeners[0]({
    url: 'https://cdn/master.m3u8',
    originUrl: 'moz-extension://0f3c2b8e-1111-4a2b-9c3d-123456789abc/background.js',
    requestHeaders: [{ name: 'Referer', value: 'https://cdn/' }],
  });
  assert.equal(recorder.lookup('https://cdn/master.m3u8'), undefined);
  assert.equal(ownOrigin(chrome), 'moz-extension://0f3c2b8e-1111-4a2b-9c3d-123456789abc');
});

test('where a request came from: Chrome\'s initiator or Firefox\'s originUrl', () => {
  assert.equal(initiatorOf({ initiator: 'https://site.example' }), 'https://site.example');
  assert.equal(initiatorOf({ originUrl: 'https://site.example:8443/watch?v=1' }), 'https://site.example:8443');
  assert.equal(initiatorOf({ initiator: 'chrome-extension://abc' }), 'chrome-extension://abc');
  assert.equal(initiatorOf({}), undefined);
});
