// Finds video streams (HLS playlists) that a page is playing, so the popup can offer to download them.
// Pure parts are exported for tests; MediaTracker takes every browser API as a parameter.

import { inspectMedia } from './tracks.js';

const MAX_PER_TAB = 12;
const MAX_PLAYLIST_CHARS = 512 * 1024;
const PLAYLIST_TYPES = ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl', 'application/mpegurl', 'application/dash+xml'];

// A player may serve its playlist under any name and type (`/p/9f3a`, `/list.txt`, `/seg.jpg`). Those cannot be told by their
// address, so a small answer of a neutral type is read and looked at (a playlist begins with #EXTM3U, a manifest with <MPD).
const NEUTRAL_TYPES = ['', 'text/plain', 'application/octet-stream', 'binary/octet-stream', 'image/jpeg', 'image/png', 'image/gif', 'image/webp', 'text/html'];
const NOT_PLAYLIST_PATHS = /\.(ts|m4s|mp4|m4a|m4v|aac|mp3|webm|mkv|js|mjs|css|json|map|woff2?|ttf|otf|svg|ico|wasm|vtt|srt|key)$/i;
const MAX_SNIFF_BYTES = 300 * 1024;   // a playlist is text and small; anything bigger is a video piece, not worth reading
const MAX_SNIFFS_PER_TAB = 40;

/** Could this response be a playlist under a neutral name? Only its headers are known here. */
export function worthALook(details, contentType) {
  if (!['xmlhttprequest', 'other', 'fetch'].includes(details.type)) return false;
  if (details.statusCode && details.statusCode !== 200) return false;
  let path;
  try {
    path = new URL(details.url).pathname;
  } catch {
    return false;
  }
  if (NOT_PLAYLIST_PATHS.test(path)) return false;

  const type = String(contentType ?? '').split(';')[0].trim().toLowerCase();
  if (!NEUTRAL_TYPES.includes(type)) return false;

  const length = Number((details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-length')?.value);
  if (Number.isFinite(length) && length > 0) return length <= MAX_SNIFF_BYTES;
  return !type.startsWith('image/');   // no length given: text and plain bytes may be a playlist, an "image" of unknown size is a piece
}

const FLV_TYPES = ['video/x-flv', 'video/flv', 'application/x-flv'];

/**
 * A live stream delivered as one endless response (an FLV pull): FLV by its type or its address, and no length, because a response
 * that never ends has none. (An FLV file has a length: that is a plain video file, not a recording.)
 */
export function looksLikeLiveFlv(details, contentType) {
  if (!['xmlhttprequest', 'media', 'other', 'fetch'].includes(details?.type)) return false;
  contentType ??= (details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-type')?.value;
  if (details.statusCode && details.statusCode !== 200) return false;
  if ((details.responseHeaders || []).some((h) => h?.name?.toLowerCase() === 'content-length')) return false;
  const type = String(contentType ?? '').split(';')[0].trim().toLowerCase();
  if (FLV_TYPES.includes(type)) return true;
  try {
    return new URL(details.url).pathname.toLowerCase().endsWith('.flv') && (type === '' || type === 'application/octet-stream' || type === 'binary/octet-stream');
  } catch {
    return false;
  }
}

/** The address or the content type says "HLS playlist" or "DASH manifest". */
export function looksLikePlaylist(url, contentType) {
  if (typeof contentType === 'string') {
    const type = contentType.split(';')[0].trim().toLowerCase();
    if (PLAYLIST_TYPES.includes(type)) return true;
  }
  try {
    const path = new URL(url).pathname.toLowerCase();
    return path.endsWith('.m3u8') || path.endsWith('.mpd');
  } catch {
    return false;
  }
}

/**
 * What a playlist is: a master (lists qualities), a media playlist (lists segments) or nothing we know.
 * `children` are the absolute addresses a master points to, so those can be hidden as noise.
 */
export function classifyPlaylist(text, baseUrl) {
  if (typeof text === 'string' && /<MPD[\s>]/.test(text.slice(0, 4000))) return classifyManifest(text);
  if (typeof text !== 'string' || !text.replace(/^﻿/, '').trimStart().startsWith('#EXTM3U')) return { kind: 'none' };
  const lines = text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  const resolve = (ref) => {
    try { return new URL(ref, baseUrl).href; } catch { return null; }
  };

  if (lines.some((l) => l.startsWith('#EXT-X-STREAM-INF'))) {
    const children = [];
    lines.forEach((line, i) => {
      if (line.startsWith('#EXT-X-STREAM-INF') && lines[i + 1] && !lines[i + 1].startsWith('#')) children.push(resolve(lines[i + 1]));
      const media = /^#EXT-X-MEDIA:.*\bURI="([^"]+)"/.exec(line);
      if (media) children.push(resolve(media[1]));
    });
    const heights = lines
      .map((l) => /RESOLUTION=\d+x(\d+)/.exec(l)?.[1])
      .filter(Boolean)
      .map(Number);
    return { kind: 'master', children: children.filter(Boolean), maxHeight: heights.length ? Math.max(...heights) : 0, live: false, duration: 0 };
  }

  if (!lines.some((l) => l.startsWith('#EXTINF'))) return { kind: 'none' };
  const live = !lines.some((l) => l.startsWith('#EXT-X-ENDLIST')) && !/#EXT-X-PLAYLIST-TYPE:\s*VOD/i.test(text);
  let duration = 0;
  for (const l of lines) {
    const m = /^#EXTINF:([\d.]+)/.exec(l);
    if (m) duration += Number(m[1]);
  }
  const encrypted = lines.some((l) => /^#EXT-X-KEY:/.test(l) && !/METHOD=(NONE|AES-128)/.test(l));

  // the first piece (the initialisation part of an MP4 stream, else the first segment): its first bytes say picture or sound
  const map = lines.map((l) => /^#EXT-X-MAP:.*\bURI="([^"]+)"/.exec(l)?.[1]).find(Boolean);
  const first = map ?? lines.find((l, i) => !l.startsWith('#') && lines.slice(0, i).some((p) => p.startsWith('#EXTINF')));
  return { kind: 'media', children: [], live, duration, protected: encrypted, maxHeight: 0, piece: first ? resolve(first) : null };
}

/** A DASH manifest: one entry for the whole presentation (the qualities are inside it). */
export function classifyManifest(text) {
  const head = /<MPD\b[^>]*>/.exec(text)?.[0] ?? '';
  const live = /\btype\s*=\s*"dynamic"/.test(head);
  const iso = /mediaPresentationDuration\s*=\s*"PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?"/.exec(head);
  const duration = iso ? Number(iso[1] ?? 0) * 3600 + Number(iso[2] ?? 0) * 60 + Number(iso[3] ?? 0) : 0;
  const heights = [...text.matchAll(/\bheight\s*=\s*"(\d+)"/g)].map((m) => Number(m[1]));
  return {
    kind: 'master',
    children: [],
    live,
    duration: live ? 0 : duration,
    protected: /<ContentProtection\b/.test(text),
    maxHeight: heights.length ? Math.max(...heights) : 0,
  };
}

export class MediaTracker {
  /**
   * @param {object} options
   * @param {(url: string) => Promise<string>} options.fetchText  reads a playlist with the browser's cookies
   * @param {(url: string, via: string) => Promise<Uint8Array>} [options.fetchHead]  the first bytes of a piece (`via`: its playlist)
   * @param {(tabId: number) => void} [options.onChange]
   */
  constructor({ fetchText, fetchHead = null, onChange = () => {} }) {
    this.fetchText = fetchText;
    this.fetchHead = fetchHead;
    this.onChange = onChange;
    this.byTab = new Map();      // tabId -> Map(url -> item)
    this.pending = new Set();    // looks in progress, so a player asking twice costs one look
    this.looked = new Map();     // tabId -> Set(url): neutral answers already read once, so each costs one look at most
  }

  /** A response arrived: `details` is what webRequest.onHeadersReceived gives. */
  async consider(details) {
    const { tabId, url } = details || {};
    if (!Number.isInteger(tabId) || tabId < 0 || typeof url !== 'string') return;
    if (!/^https?:/i.test(url)) return;

    const contentType = (details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-type')?.value;

    // a live FLV pull is never read (it does not end): the address is all there is, and the app records it
    if (looksLikeLiveFlv(details, contentType)) {
      if (!this.byTab.get(tabId)?.has(url)) {
        this.add(tabId, url, { kind: 'record', live: true, children: [], duration: 0, maxHeight: 0, referrer: details.documentUrl || details.initiator || '' });
      }

      return;
    }

    const named = looksLikePlaylist(url, contentType);
    if (!named) {
      if (!worthALook(details, contentType)) return;
      const looked = this.looked.get(tabId) ?? new Set();
      this.looked.set(tabId, looked);
      if (looked.has(url) || looked.size >= MAX_SNIFFS_PER_TAB) return;
      looked.add(url);
    }
    if (this.byTab.get(tabId)?.has(url)) return;

    const key = `${tabId} ${url}`;
    if (this.pending.has(key)) return;
    this.pending.add(key);
    try {
      let text;
      try {
        text = await this.fetchText(url);
      } catch {
        return;   // the player may use a one-time address: nothing to offer then
      }
      // the page the player lives in (an iframe's own address): the pieces will be asked for with it as referrer
      const info = { ...classifyPlaylist(String(text).slice(0, MAX_PLAYLIST_CHARS), url), referrer: details.documentUrl || details.initiator || '' };
      if (info.kind === 'media' && info.piece && this.fetchHead) {
        // picture, sound or both: a player that asks for picture and sound separately leaves two playlists that belong together
        try {
          info.tracks = inspectMedia(await this.fetchHead(info.piece, url));
        } catch {
          info.tracks = { kind: 'unknown', width: 0, height: 0 };
        }
      }
      this.add(tabId, url, info);
    } finally {
      this.pending.delete(key);
    }
  }

  add(tabId, url, info) {
    if (info.kind === 'none') return;
    const items = this.byTab.get(tabId) ?? new Map();
    this.byTab.set(tabId, items);

    // a quality list makes the qualities it names redundant, in whichever order they were seen
    if (info.kind === 'media' && [...items.values()].some((i) => i.children?.includes(url))) return;
    if (info.kind === 'master') for (const child of info.children) items.delete(child);

    items.set(url, { url, ...info, seenAt: Date.now() });
    while (items.size > MAX_PER_TAB) items.delete(items.keys().next().value);
    this.onChange(tabId);
  }

  /** Newest first. */
  list(tabId) {
    return [...(this.byTab.get(tabId)?.values() ?? [])].reverse();
  }

  count(tabId) {
    return this.byTab.get(tabId)?.size ?? 0;
  }

  get(tabId, url) {
    return this.byTab.get(tabId)?.get(url);
  }

  clear(tabId) {
    this.looked.delete(tabId);
    if (this.byTab.delete(tabId)) this.onChange(tabId);
  }
}

/**
 * Playlists that belong together: a stream of picture alone and a stream of sound alone, as a player asks for them one by
 * one, become ONE entry that downloads both (the picture's address, with the sound's as `audioUrl`). Several qualities of
 * picture each pair with the sound; a stream that holds both, or that could not be inspected, stays as it is.
 * @param items  tracker items, newest first
 */
export function pairStreams(items) {
  const isStream = (i) => i.kind === 'media';
  const videos = items.filter((i) => isStream(i) && i.tracks?.kind === 'video');
  const audios = items.filter((i) => isStream(i) && i.tracks?.kind === 'audio');
  if (videos.length === 0 || audios.length === 0) {
    return items.map((i) => (isStream(i) && i.tracks?.kind === 'audio' ? { ...i, audioOnly: true } : { ...i }));
  }

  const alike = (a, b) => !a.duration || !b.duration || Math.abs(a.duration - b.duration) <= Math.max(3, 0.03 * Math.max(a.duration, b.duration));
  const distance = (a, b) => Math.abs((a.duration || 0) - (b.duration || 0));
  const used = new Set();
  const paired = new Map();   // video item -> its sound
  for (const video of videos) {
    const fitting = audios.filter((a) => alike(a, video)).sort((a, b) => distance(a, video) - distance(b, video));   // equal: the newest (earlier in the list)
    if (fitting[0]) {
      paired.set(video, fitting[0]);
      used.add(fitting[0]);
    }
  }

  const out = [];
  for (const item of items) {
    if (paired.has(item)) out.push({ ...item, audioUrl: paired.get(item).url, maxHeight: item.tracks.height || item.maxHeight });
    else if (!used.has(item)) out.push(isStream(item) && item.tracks?.kind === 'audio' ? { ...item, audioOnly: true } : { ...item });
  }
  return out;
}
