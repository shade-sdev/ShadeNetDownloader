// A player can hold the list of a stream's pieces itself (Media Source Extensions with no playlist or manifest on the network: the
// page's own code knows "init.mp4, then part_1.m4s, part_2.m4s ..."). Nothing published says what the video consists of, but the
// player's requests do: a run of pieces in one folder, named alike with a counter, after their initialisation part. Those, in the
// order the player asked for them, joined after the initialisation part, are the video, as far as the player has loaded it.
//
// What is checked before anything is offered: the first piece really is a piece of a stream (an MP4 fragment or MPEG-TS), not a whole
// file; a fragmented stream has an initialisation part; and that part names no encryption (a protected stream is left alone).
//
// Pure apart from `fetchHead` (the first bytes of an address, asked for the way the player asked for it). The controller wires it.

import { inspectMedia } from './tracks.js';

const PIECE_TYPES = ['video/mp4', 'audio/mp4', 'video/iso.segment', 'audio/iso.segment', 'video/mp2t', 'application/octet-stream', 'binary/octet-stream', ''];
const PIECE_PATH = /\.(m4s|m4v|m4a|cmfv|cmfa|mp4|ts)$/i;
const MIN_PIECES = 3;           // fewer is not a run (a few clips on a page are not pieces of one video)
const MAX_PIECES = 4000;
const MAX_GROUPS = 12;

/** The folder, file name and name pattern (the last number in the name replaced by #) of an address. */
export function pieceKey(url) {
  try {
    const u = new URL(url);
    const slash = u.pathname.lastIndexOf('/');
    const name = u.pathname.slice(slash + 1);
    if (!name) return null;
    // the counter is the last number of the name, not of its ending (".m4s" has a 4 in it)
    const dot = name.lastIndexOf('.');
    const base = dot > 0 ? name.slice(0, dot) : name;
    const ending = dot > 0 ? name.slice(dot) : '';
    return { dir: u.origin + u.pathname.slice(0, slash + 1), name, path: u.pathname, stem: base.replace(/(\d+)(?=\D*$)/, '#') + ending };
  } catch {
    return null;
  }
}

/** Could this response be a piece of a stream, or its initialisation part? A whole response (200) of a known size, named or typed like one. */
export function looksLikePiece(details, contentType) {
  if (!['xmlhttprequest', 'other', 'fetch'].includes(details?.type)) return false;
  if (details.statusCode && details.statusCode !== 200) return false;
  if (!(details.responseHeaders || []).some((h) => h?.name?.toLowerCase() === 'content-length')) return false;   // an endless response is no piece
  const type = String(contentType ?? (details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-type')?.value ?? '')
    .split(';')[0].trim().toLowerCase();
  try {
    const path = new URL(details.url).pathname;
    if (type === 'video/iso.segment' || type === 'audio/iso.segment') return true;
    return PIECE_PATH.test(path) && PIECE_TYPES.includes(type);
  } catch {
    return false;
  }
}

/** The first bytes of a piece: 'fragment' (an MP4 fragment), 'ts' (MPEG-TS), 'whole' (a complete MP4 file: not a piece) or 'unknown'. */
export function pieceFormat(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes ?? []);
  if (b.length >= 1 && b[0] === 0x47) return 'ts';
  if (b.length < 8) return 'unknown';
  const box = String.fromCharCode(b[4], b[5], b[6], b[7]);
  if (['styp', 'moof', 'sidx', 'prft', 'emsg'].includes(box)) return 'fragment';
  if (['ftyp', 'moov'].includes(box)) return 'whole';
  return 'unknown';
}

/** Does this look like an initialisation part (its boxes name the tracks)? */
function isInitPart(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes ?? []);
  for (let i = 4; i + 4 <= b.length; i++) if (b[i] === 0x6d && String.fromCharCode(b[i], b[i + 1], b[i + 2], b[i + 3]) === 'moov') return true;
  return false;
}

/** How many words of a file name two names share (an initialisation part is named after its pieces: init-stream0 and chunk-stream0-#). */
function likeness(initName, stem) {
  const words = (s) => new Set(s.toLowerCase().replace(/init/g, ' ').split(/[^a-z0-9#]+/).filter(Boolean));
  const a = words(initName);
  let shared = 0;
  for (const w of words(stem)) if (a.has(w)) shared++;
  return shared;
}

export class PieceTracker {
  /**
   * @param {object} options
   * @param {(url: string, via: string) => Promise<Uint8Array>} options.fetchHead  the first bytes of an address
   * @param {(tabId: number) => void} [options.onChange]
   * @param {(tabId: number) => boolean} [options.skip]  true when the tab plays a playlist (its pieces belong to that)
   */
  constructor({ fetchHead, onChange = () => {}, skip = () => false }) {
    this.fetchHead = fetchHead;
    this.onChange = onChange;
    this.skip = skip;
    this.byTab = new Map();   // tabId -> { order, groups: Map(dir+stem -> group), inits: [{ url, dir, name, order }] }
  }

  state(tabId) {
    let s = this.byTab.get(tabId);
    if (!s) {
      s = { order: 0, groups: new Map(), inits: [] };
      this.byTab.set(tabId, s);
    }
    return s;
  }

  consider(details, contentType) {
    const { tabId, url } = details || {};
    if (!Number.isInteger(tabId) || tabId < 0 || typeof url !== 'string' || !looksLikePiece(details, contentType)) return;
    if (this.skip(tabId)) return;
    const key = pieceKey(url);
    if (!key) return;
    const s = this.state(tabId);

    if (/init/i.test(key.name)) {
      if (!s.inits.some((i) => i.url === url)) s.inits.push({ url, dir: key.dir, name: key.name, order: s.order++ });
      return;
    }

    const id = key.dir + key.stem;
    let group = s.groups.get(id);
    if (!group) {
      if (s.groups.size >= MAX_GROUPS * 4) return;
      group = { id, dir: key.dir, stem: key.stem, paths: new Set(), pieces: [], status: 'collecting' };
      s.groups.set(id, group);
    }

    if (group.paths.has(key.path) || group.pieces.length >= MAX_PIECES) return;   // a piece asked for again (a seek back) is the same piece
    group.paths.add(key.path);
    group.pieces.push({ url, order: s.order++ });

    if (group.status === 'collecting' && group.pieces.length >= MIN_PIECES) this.check(tabId, group);
    else if (group.status === 'ok') this.onChange(tabId);
  }

  /** The initialisation part of a run: named "init" in its folder (the one named most like the pieces), else the file asked for just before. */
  initFor(s, group) {
    const first = group.pieces[0].order;
    const before = (i) => i.order < first;
    const named = s.inits.filter((i) => i.dir === group.dir && before(i));
    if (named.length > 0) {
      return named.sort((a, b) => likeness(b.name, group.stem) - likeness(a.name, group.stem) || b.order - a.order)[0];
    }

    // no "init" in a name: a lone file of the same folder, asked for before the run began
    const lone = [...s.groups.values()]
      .filter((g) => g !== group && g.dir === group.dir && g.pieces.length === 1 && before(g.pieces[0]))
      .map((g) => ({ url: g.pieces[0].url, name: pieceKey(g.pieces[0].url).name, order: g.pieces[0].order }));
    if (lone.length > 0) return lone.sort((a, b) => b.order - a.order)[0];

    // an initialisation part kept in another folder
    return s.inits.filter(before).sort((a, b) => likeness(b.name, group.stem) - likeness(a.name, group.stem) || b.order - a.order)[0] ?? null;
  }

  async check(tabId, group) {
    group.status = 'checking';
    try {
      const first = group.pieces[0].url;
      const firstBytes = await this.fetchHead(first, first);
      const format = pieceFormat(firstBytes);
      if (format === 'ts') {
        group.init = null;   // MPEG-TS pieces carry everything themselves
        group.info = inspectMedia(firstBytes);
        group.status = 'ok';
      } else if (format === 'fragment') {
        const init = this.initFor(this.state(tabId), group);
        const head = init ? await this.fetchHead(init.url, init.url) : null;
        if (!init || !isInitPart(head)) {
          group.status = 'no-init';
        } else {
          group.init = init.url;
          group.info = inspectMedia(head);
          group.status = 'ok';
        }
      } else {
        group.status = format === 'whole' ? 'whole-files' : 'not-pieces';
      }
    } catch {
      group.status = 'unreadable';
    }

    this.onChange(tabId);
  }

  /**
   * What can be saved on a tab: each run that passed the check, picture and sound paired when the player keeps them apart (a picture-only
   * run and a sound-only run become one entry). `url` (the first piece) names the entry; `protected` ones are listed only to say so.
   */
  items(tabId) {
    const ready = [...(this.byTab.get(tabId)?.groups.values() ?? [])].filter((g) => g.status === 'ok' && g.pieces.length >= MIN_PIECES);
    const entry = (g) => ({
      url: g.pieces[0].url,
      init: g.init ?? null,
      pieces: g.pieces.map((p) => p.url),
      kind: g.info?.kind ?? 'unknown',
      maxHeight: g.info?.height ?? 0,
      protected: !!g.info?.encrypted,
    });

    const videos = ready.filter((g) => g.info?.kind === 'video');
    const audios = ready.filter((g) => g.info?.kind === 'audio');
    const out = [];
    const used = new Set();
    for (const v of videos) {
      const sound = audios.filter((a) => !used.has(a)).sort((a, b) => Math.abs(a.pieces.length - v.pieces.length) - Math.abs(b.pieces.length - v.pieces.length))[0];
      const item = entry(v);
      if (sound) {
        used.add(sound);
        item.audioInit = sound.init ?? null;
        item.audioPieces = sound.pieces.map((p) => p.url);
        item.protected ||= !!sound.info?.encrypted;
      }
      out.push(item);
    }

    for (const g of ready) if (!videos.includes(g) && !used.has(g)) out.push({ ...entry(g), audioOnly: g.info?.kind === 'audio' });
    return out;
  }

  find(tabId, url) {
    return this.items(tabId).find((i) => i.url === url);
  }

  /** For diagnosis: every run seen on the tab and what the check said. */
  describe(tabId) {
    return [...(this.byTab.get(tabId)?.groups.values() ?? [])]
      .filter((g) => g.pieces.length >= MIN_PIECES)
      .map((g) => ({ pattern: g.dir.split('?')[0] + g.stem, pieces: g.pieces.length, status: g.status, init: g.init ? g.init.split('?')[0] : null, kind: g.info?.kind, encrypted: !!g.info?.encrypted }));
  }

  clear(tabId) {
    if (this.byTab.delete(tabId)) this.onChange(tabId);
  }
}
