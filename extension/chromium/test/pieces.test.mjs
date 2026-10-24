// A player that keeps its list of pieces itself (Media Source Extensions, no playlist or manifest on the network), like
// https://r-w-c.github.io/mse-demo/: the pieces it asked for are offered, joined after their initialisation part.
import test from 'node:test';
import assert from 'node:assert/strict';
import { PieceTracker, looksLikePiece, pieceFormat, pieceKey } from '../lib/pieces.js';
import * as real from './fixtures.mjs';

const piece = (tabId, url, type = 'video/iso.segment', extra = {}) => ({
  tabId, url, type: 'xmlhttprequest', statusCode: 200,
  responseHeaders: [{ name: 'Content-Type', value: type }, { name: 'Content-Length', value: '1000' }], ...extra,
});

const FRAGMENT = new Uint8Array([0, 0, 0, 24, ...'styp'.split('').map((c) => c.charCodeAt(0)), 0, 0, 0, 0]);
const WHOLE = new Uint8Array([0, 0, 0, 24, ...'ftyp'.split('').map((c) => c.charCodeAt(0)), 0, 0, 0, 0]);

function tracker(heads = {}, { skip = () => false } = {}) {
  const asked = [];
  const changes = [];
  const t = new PieceTracker({
    fetchHead: async (url) => { asked.push(url); return heads[url] ?? new Uint8Array(0); },
    onChange: (tabId) => changes.push(tabId),
    skip,
  });
  return { t, asked, changes };
}

const settle = async (t, tabId, n = 1) => { for (let i = 0; i < 100 && t.items(tabId).length < n; i++) await new Promise((r) => setTimeout(r, 5)); };
const BASE = 'https://cdn.example/video/';

test('the counter of a piece is the last number of its name, not of its ending', () => {
  assert.equal(pieceKey(`${BASE}part_153856.m4s`).stem, 'part_#.m4s');
  assert.equal(pieceKey(`${BASE}s00101.m4s?token=1`).stem, 's#.m4s');
  assert.equal(pieceKey(`${BASE}chunk-stream1-00012.m4s`).stem, 'chunk-stream1-#.m4s');   // the picture's and the sound's pieces stay apart
  assert.equal(pieceKey(`${BASE}`), null);
});

test('only whole answers of a known length, named or typed like a piece, are pieces', () => {
  assert.equal(looksLikePiece(piece(7, `${BASE}part_0.m4s`)), true);
  assert.equal(looksLikePiece(piece(7, `${BASE}seg?id=3`, 'video/iso.segment')), true);                       // typed as a piece
  assert.equal(looksLikePiece(piece(7, `${BASE}part_0.m4s`, 'text/html')), false);
  assert.equal(looksLikePiece(piece(7, `${BASE}part_0.m4s`, 'video/iso.segment', { statusCode: 206 })), false); // a range of one file
  assert.equal(looksLikePiece(piece(7, `${BASE}live.ts`, 'video/mp2t', { responseHeaders: [{ name: 'Content-Type', value: 'video/mp2t' }] })), false);   // endless
  assert.equal(looksLikePiece(piece(7, `${BASE}part_0.m4s`, 'video/iso.segment', { type: 'media' })), false);   // a <video src> download
});

test('the first bytes tell a piece from a whole file', () => {
  assert.equal(pieceFormat(FRAGMENT), 'fragment');
  assert.equal(pieceFormat(WHOLE), 'whole');
  assert.equal(pieceFormat(real.tsVideo), 'ts');
  assert.equal(pieceFormat(new Uint8Array(3)), 'unknown');
});

test('a run of pieces after their initialisation part is offered, in the order the player asked for them', async () => {
  const heads = { [`${BASE}part_0.m4s`]: FRAGMENT, [`${BASE}part_init.mp4`]: real.fmp4Both };
  const { t } = tracker(heads);
  t.consider(piece(7, `${BASE}part_init.mp4`, 'video/mp4'));
  for (const n of [0, 153856, 296704, 411136]) t.consider(piece(7, `${BASE}part_${n}.m4s`));
  t.consider(piece(7, `${BASE}part_153856.m4s`));   // asked for again (a seek back): the same piece

  await settle(t, 7);
  const [item] = t.items(7);
  assert.equal(item.init, `${BASE}part_init.mp4`);
  assert.deepEqual(item.pieces, [0, 153856, 296704, 411136].map((n) => `${BASE}part_${n}.m4s`));
  assert.deepEqual([item.kind, item.maxHeight, item.protected], ['both', 240, false]);
});

test('separate picture and sound runs become one entry with both', async () => {
  const heads = {
    [`${BASE}chunk-stream0-00001.m4s`]: FRAGMENT, [`${BASE}chunk-stream1-00001.m4s`]: FRAGMENT,
    [`${BASE}init-stream0.m4s`]: real.fmp4Video, [`${BASE}init-stream1.m4s`]: real.fmp4Audio,
  };
  const { t } = tracker(heads);
  t.consider(piece(7, `${BASE}init-stream0.m4s`));
  t.consider(piece(7, `${BASE}init-stream1.m4s`));
  for (const n of [1, 2, 3]) {
    t.consider(piece(7, `${BASE}chunk-stream0-0000${n}.m4s`));
    t.consider(piece(7, `${BASE}chunk-stream1-0000${n}.m4s`));
  }

  await settle(t, 7);
  for (let i = 0; i < 50 && !(t.items(7)[0]?.audioPieces); i++) await new Promise((r) => setTimeout(r, 5));
  const items = t.items(7);
  assert.equal(items.length, 1);
  assert.equal(items[0].init, `${BASE}init-stream0.m4s`);                                    // each run with the init named like it
  assert.equal(items[0].audioInit, `${BASE}init-stream1.m4s`);
  assert.equal(items[0].audioPieces.length, 3);
});

test('a few whole video files named alike are not pieces of one video', async () => {
  const heads = { [`${BASE}clip1.mp4`]: WHOLE };
  const { t } = tracker(heads);
  for (const n of [1, 2, 3]) t.consider(piece(7, `${BASE}clip${n}.mp4`, 'video/mp4'));
  await new Promise((r) => setTimeout(r, 30));
  assert.deepEqual(t.items(7), []);
  assert.equal(t.describe(7)[0].status, 'whole-files');
});

test('MPEG-TS pieces need no initialisation part', async () => {
  const heads = { [`${BASE}seg1.ts`]: real.tsBoth };
  const { t } = tracker(heads);
  for (const n of [1, 2, 3]) t.consider(piece(7, `${BASE}seg${n}.ts`, 'video/mp2t'));
  await settle(t, 7);
  assert.deepEqual([t.items(7)[0].init, t.items(7)[0].pieces.length, t.items(7)[0].kind], [null, 3, 'both']);
});

test('a protected stream (encryption in its initialisation part) is marked so, and fragments with no initialisation part are not offered', async () => {
  const protectedInit = new Uint8Array([...real.fmp4Video, 0, 0, 0, 16, ...'pssh'.split('').map((c) => c.charCodeAt(0)), 0, 0, 0, 0, 0, 0, 0, 0]);
  const { t } = tracker({ [`${BASE}a/init.mp4`]: protectedInit, [`${BASE}a/p1.m4s`]: FRAGMENT, [`${BASE}b/p1.m4s`]: FRAGMENT });
  t.consider(piece(7, `${BASE}a/init.mp4`, 'video/mp4'));
  for (const n of [1, 2, 3]) t.consider(piece(7, `${BASE}a/p${n}.m4s`));
  for (const n of [1, 2, 3]) t.consider(piece(8, `${BASE}b/p${n}.m4s`));
  await settle(t, 7);
  await new Promise((r) => setTimeout(r, 30));

  assert.equal(t.items(7)[0].protected, true);
  assert.deepEqual(t.items(8), []);
  assert.equal(t.describe(8)[0].status, 'no-init');
});

test('a tab that plays a playlist keeps its pieces for it', async () => {
  const { t, asked } = tracker({}, { skip: () => true });
  for (const n of [1, 2, 3, 4]) t.consider(piece(7, `${BASE}s${n}.m4s`));
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual([t.items(7), asked], [[], []]);
});
