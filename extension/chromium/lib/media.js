// Finds video streams (HLS playlists) that a page is playing, so the popup can offer to download them.
// Pure parts are exported for tests; MediaTracker takes every browser API as a parameter.

const MAX_PER_TAB = 12;
const MAX_PLAYLIST_CHARS = 512 * 1024;
const PLAYLIST_TYPES = ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl', 'application/mpegurl', 'application/dash+xml'];

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
  return { kind: 'media', children: [], live, duration, protected: encrypted, maxHeight: 0 };
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
   * @param {(tabId: number) => void} [options.onChange]
   */
  constructor({ fetchText, onChange = () => {} }) {
    this.fetchText = fetchText;
    this.onChange = onChange;
    this.byTab = new Map();      // tabId -> Map(url -> item)
    this.pending = new Set();    // looks in progress, so a player asking twice costs one look
  }

  /** A response arrived: `details` is what webRequest.onHeadersReceived gives. */
  async consider(details) {
    const { tabId, url } = details || {};
    if (!Number.isInteger(tabId) || tabId < 0 || typeof url !== 'string') return;
    if (!/^https?:/i.test(url)) return;

    const contentType = (details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-type')?.value;
    if (!looksLikePlaylist(url, contentType)) return;
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
      this.add(tabId, url, classifyPlaylist(String(text).slice(0, MAX_PLAYLIST_CHARS), url));
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
    if (this.byTab.delete(tabId)) this.onChange(tabId);
  }
}
