import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectMedia } from '../lib/tracks.js';
import * as real from './fixtures.mjs';

test('an MP4 initialisation part says which tracks it holds, and how big the picture is', () => {
  assert.deepEqual(inspectMedia(real.fmp4Video), { kind: 'video', width: 320, height: 240, encrypted: false });
  assert.equal(inspectMedia(real.fmp4Audio).kind, 'audio');
  assert.equal(inspectMedia(real.fmp4Both).kind, 'both');
  assert.equal(inspectMedia(real.fmp4Both).height, 240);
});

test('an initialisation part that carries encryption boxes says so (a protected stream)', () => {
  const protectedInit = new Uint8Array([...real.fmp4Video, 0, 0, 0, 16, ...'pssh'.split('').map((c) => c.charCodeAt(0)), 0, 0, 0, 0, 0, 0, 0, 0]);
  assert.equal(inspectMedia(protectedInit).encrypted, true);
  assert.equal(inspectMedia(real.fmp4Both).encrypted, false);
});

test('a brand named like a codec in the file type box is not mistaken for the picture (Reddit lists "avc1" among its brands)', () => {
  // an 'ftyp' box whose compatible brands include avc1, followed by bytes that would read as a nonsense size
  const brands = [0, 0, 0, 28, ...'ftypisomavc1isomiso2avc1mp41'.slice(0, 24).split('').map((c) => c.charCodeAt(0))];
  const init = new Uint8Array([...brands, ...real.fmp4Video.subarray(28)]);
  assert.equal(inspectMedia(init).height, 240);
  assert.equal(inspectMedia(init).kind, 'video');
});

test('an MPEG-TS piece says it through its program table', () => {
  assert.equal(inspectMedia(real.tsVideo).kind, 'video');
  assert.equal(inspectMedia(real.tsAudio).kind, 'audio');
  assert.equal(inspectMedia(real.tsBoth).kind, 'both');
});

test('a bare AAC or MP3 file is sound', () => {
  assert.equal(inspectMedia(real.adts).kind, 'audio');
  assert.equal(inspectMedia(real.mp3).kind, 'audio');
  assert.equal(inspectMedia(Uint8Array.from([0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0])).kind, 'audio');   // ID3 tag first
});

test('anything else, too little, or damaged bytes are "unknown" and never throw', () => {
  assert.equal(inspectMedia(undefined).kind, 'unknown');
  assert.equal(inspectMedia(new Uint8Array(0)).kind, 'unknown');
  assert.equal(inspectMedia(Uint8Array.from([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])).kind, 'unknown');
  assert.equal(inspectMedia(new TextEncoder().encode('<html><body>not media</body></html>')).kind, 'unknown');
  assert.equal(inspectMedia(real.fmp4Video.subarray(0, 20)).kind, 'unknown');                      // cut off before the tracks
  const damaged = Uint8Array.from(real.tsVideo);
  damaged[188] = 0;                                                                                   // the second packet lost its sync byte
  assert.equal(inspectMedia(damaged).kind, 'unknown');
  assert.equal(inspectMedia(real.tsVideo.subarray(0, 100)).kind, 'unknown');
});
