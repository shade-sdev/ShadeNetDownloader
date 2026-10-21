import test from 'node:test';
import assert from 'node:assert/strict';
import {
  basename, buildCapture, cookiesToHeader, evaluate, headerValue, hostMatches, isCapturableUrl, pickHeaders,
} from '../lib/capture.js';

test('only web addresses are capturable', () => {
  assert.ok(isCapturableUrl('https://example.com/a.zip'));
  assert.ok(isCapturableUrl('http://127.0.0.1:8080/f?x=1'));
  for (const bad of ['blob:https://x/1', 'data:text/plain,hi', 'file:///C:/a.zip', 'chrome-extension://abc/x', 'ftp://x/a', 'javascript:alert(1)', '', 'nonsense', null, undefined, 42]) {
    assert.equal(isCapturableUrl(bad), false, String(bad));
  }
  assert.equal(isCapturableUrl('https://example.com/' + 'a'.repeat(9000)), false);
});

test('host patterns match the host and its sub-domains, nothing else', () => {
  assert.ok(hostMatches('example.com', ['example.com']));
  assert.ok(hostMatches('cdn.example.com', ['example.com']));
  assert.ok(hostMatches('cdn.example.com', ['*.example.com']));
  assert.ok(hostMatches('cdn.example.com', ['.example.com']));
  assert.ok(hostMatches('CDN.Example.COM', [' Example.com ']));
  assert.equal(hostMatches('notexample.com', ['example.com']), false);
  assert.equal(hostMatches('example.com.evil.org', ['example.com']), false);
  assert.equal(hostMatches('example.com', []), false);
  assert.equal(hostMatches('example.com', ['', null, 5]), false);
  assert.equal(hostMatches('', ['example.com']), false);
});

test('basename handles windows and posix paths', () => {
  assert.equal(basename('C:\\Users\\Shade\\Downloads\\The Clumsy Witch Seris.zip'), 'The Clumsy Witch Seris.zip');
  assert.equal(basename('/home/u/file.bin'), 'file.bin');
  assert.equal(basename('plain.txt'), 'plain.txt');
  assert.equal(basename(''), '');
  assert.equal(basename('C:\\dir\\'), '');
  assert.equal(basename(undefined), '');
});

test('cookies become a Cookie header value', () => {
  assert.equal(cookiesToHeader([{ name: 'a', value: '1' }, { name: 'accountToken', value: 'x=y' }]), 'a=1; accountToken=x=y');
  assert.equal(cookiesToHeader([]), '');
  assert.equal(cookiesToHeader(undefined), '');
  assert.equal(cookiesToHeader([{ name: '', value: 'x' }, { value: 'y' }, null]), '');
  assert.equal(cookiesToHeader([{ name: 'empty' }]), 'empty=');
});

test('only allow-listed, well-formed headers are replayed', () => {
  const picked = pickHeaders([
    { name: 'Authorization', value: 'Bearer abc' },
    { name: 'accept-language', value: 'en-US,en;q=0.9' },
    { name: 'Origin', value: 'https://example.com' },
    { name: 'Host', value: 'evil' },
    { name: 'Range', value: 'bytes=0-' },
    { name: 'Cookie', value: 'a=1' },
    { name: 'Authorization-Evil', value: 'x' },
  ]);
  assert.deepEqual(picked, { Authorization: 'Bearer abc', 'accept-language': 'en-US,en;q=0.9', Origin: 'https://example.com' });
});

test('header injection and oversized values are dropped', () => {
  assert.deepEqual(pickHeaders([{ name: 'Authorization', value: 'a\r\nX-Evil: 1' }]), {});
  assert.deepEqual(pickHeaders([{ name: 'Authorization', value: 'a\nb' }]), {});
  assert.deepEqual(pickHeaders([{ name: 'Authorization', value: 'x'.repeat(9000) }]), {});
  assert.deepEqual(pickHeaders([{ name: 'Authorization', value: '' }]), {});
  assert.deepEqual(pickHeaders(undefined), {});
  assert.deepEqual(pickHeaders([null, { name: 5, value: 'x' }, { name: 'Origin' }]), {});
});

test('headerValue is case-insensitive and tolerant', () => {
  const list = [{ name: 'Cookie', value: 'a=1' }];
  assert.equal(headerValue(list, 'cookie'), 'a=1');
  assert.equal(headerValue(list, 'COOKIE'), 'a=1');
  assert.equal(headerValue(list, 'referer'), undefined);
  assert.equal(headerValue(undefined, 'cookie'), undefined);
});

const settings = { enabled: true, excludedHosts: [], minSizeBytes: 0 };

test('evaluate: the normal case is captured', () => {
  assert.deepEqual(evaluate({ url: 'https://a.example/f.zip', state: 'in_progress' }, settings), { capture: true, reason: 'ok' });
  assert.deepEqual(evaluate({ url: 'https://a.example/f.zip' }, settings), { capture: true, reason: 'ok' });
});

test('evaluate: reasons to leave a download with the browser', () => {
  const item = { url: 'https://a.example/f.zip', state: 'in_progress' };
  assert.equal(evaluate(item, { ...settings, enabled: false }).reason, 'disabled');
  assert.equal(evaluate({ ...item, url: 'blob:https://a.example/1' }, settings).reason, 'not-http');
  assert.equal(evaluate({ ...item, state: 'complete' }, settings).reason, 'not-in-progress');
  assert.equal(evaluate(item, { ...settings, excludedHosts: ['xample'] }).capture, true, 'a partial label does not match');
  assert.equal(evaluate(item, { ...settings, excludedHosts: ['a.example'] }).reason, 'excluded');
  assert.equal(evaluate({ ...item, totalBytes: 500 }, { ...settings, minSizeBytes: 1000 }).reason, 'too-small');
  assert.equal(evaluate({ ...item, totalBytes: 5000 }, { ...settings, minSizeBytes: 1000 }).capture, true);
  assert.equal(evaluate({ ...item, totalBytes: -1 }, { ...settings, minSizeBytes: 1000 }).capture, true, 'unknown size is taken');
});

test('evaluate looks at the final URL when there was a redirect', () => {
  const item = { url: 'https://short.example/x', finalUrl: 'https://cdn.example/f.zip', state: 'in_progress' };
  assert.equal(evaluate(item, { ...settings, excludedHosts: ['cdn.example'] }).reason, 'excluded');
  assert.equal(evaluate(item, { ...settings, excludedHosts: ['short.example'] }).capture, true);
});

test('buildCapture leaves out what is not known', () => {
  assert.deepEqual(buildCapture({ id: '1', url: 'https://a/f.zip' }), { type: 'capture', id: '1', url: 'https://a/f.zip', source: 'download' });

  const full = buildCapture({
    id: '2', url: 'https://cdn/f.zip', originalUrl: 'https://a/f', referrer: 'https://a/page', cookies: 'k=v',
    userAgent: 'UA', headers: { Authorization: 'x' }, fileName: 'f.zip', mimeType: 'application/zip', size: 99, source: 'contextmenu',
  });
  assert.equal(full.originalUrl, 'https://a/f');
  assert.equal(full.size, 99);
  assert.equal(full.source, 'contextmenu');

  const same = buildCapture({ id: '3', url: 'https://a/f', originalUrl: 'https://a/f', size: -1, headers: {} });
  assert.equal('originalUrl' in same, false);
  assert.equal('size' in same, false);
  assert.equal('headers' in same, false);
});
