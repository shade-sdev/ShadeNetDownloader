// A player can read one whole video file itself, a range at a time (its own code asks for bytes 0-2 MB, then 2-4 MB ... and feeds
// them to the video through a buffer), under any name and any type ("application/octet-stream", `/c/f/89a7f.4`). The <video> then
// shows a blob: or some other address, and the file's own address is only in the player's requests.
//
// Such a request answers with part of the file (206) and says how big the whole is (`Content-Range: bytes 0-2097151/734003200`).
// Its first bytes are then read and looked at: only a real video file (an MP4, a WebM or Matroska, an FLV, an MPEG-TS) is offered,
// and one whose tracks name an encryption is listed as protected. Anything else is left alone (diagnose() says what was seen).
//
// Pure apart from `fetchHead` (the first bytes of an address, asked for the way the player asked for it). The controller wires it.

import { inspectMedia } from './tracks.js';

const MIN_SIZE = 2 * 1024 * 1024;   // smaller is a clip, a sound or a picture, not a video worth a download of its own
const MAX_LOOKS_PER_TAB = 12;
const FILE_TYPES = ['', 'application/octet-stream', 'binary/octet-stream', 'application/binary', 'application/x-binary'];

/** The size of the whole file a partial answer is part of (`Content-Range: bytes 0-1023/734003200`), 0 when it does not say. */
export function wholeSize(details) {
  const range = (details?.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-range')?.value;
  const total = /\/\s*(\d+)\s*$/.exec(String(range ?? ''))?.[1];
  return total ? Number(total) : 0;
}

/** Could this answer be part of a whole video file a player reads in ranges? Only its headers are known here. */
export function looksLikeRangedFile(details, contentType) {
  if (!['xmlhttprequest', 'other'].includes(details?.type) || details.statusCode !== 206) return false;
  const type = String(contentType ?? '').split(';')[0].trim().toLowerCase();
  if (!FILE_TYPES.includes(type) && !/^video\//.test(type)) return false;
  return wholeSize(details) >= MIN_SIZE;
}

/** What the first bytes of a file are: 'mp4', 'webm', 'mkv', 'flv', 'ts', or null when they are no video file a player can be given whole. */
export function fileFormat(bytes) {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes ?? []);
  if (b.length < 12) return null;
  const at = (i, n) => String.fromCharCode(...b.subarray(i, i + n));
  if (at(4, 4) === 'ftyp') return 'mp4';
  if (b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3) {
    // the EBML header names its kind ("webm" or "matroska") within its first bytes
    const head = at(0, Math.min(b.length, 64));
    return head.includes('webm') ? 'webm' : 'mkv';
  }
  if (at(0, 3) === 'FLV' && b[3] === 1) return 'flv';
  if (b[0] === 0x47 && b.length > 376 && b[188] === 0x47 && b[376] === 0x47) return 'ts';
  return null;
}

export class RangedFileTracker {
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
    this.byTab = new Map();   // tabId -> Map(url -> { url, size, status, format, info, referrer, seenAt })
  }

  consider(details, contentType) {
    const { tabId, url } = details || {};
    if (!Number.isInteger(tabId) || tabId < 0 || typeof url !== 'string' || !/^https?:/i.test(url)) return;
    contentType ??= (details.responseHeaders || []).find((h) => h?.name?.toLowerCase() === 'content-type')?.value;
    if (!looksLikeRangedFile(details, contentType) || this.skip(tabId)) return;

    const files = this.byTab.get(tabId) ?? new Map();
    this.byTab.set(tabId, files);
    if (files.has(url) || files.size >= MAX_LOOKS_PER_TAB) return;   // the next range of a file already looked at
    const file = { url, size: wholeSize(details), status: 'checking', referrer: details.documentUrl || details.initiator || '', seenAt: Date.now() };
    files.set(url, file);
    this.check(tabId, file);
  }

  async check(tabId, file) {
    try {
      const head = await this.fetchHead(file.url, file.url);
      file.format = fileFormat(head);
      file.info = file.format ? inspectMedia(head) : null;
      file.status = file.format ? 'ok' : 'not-a-video-file';
    } catch {
      file.status = 'unreadable';
    }
    this.onChange(tabId);
  }

  /** The video files a player on the tab reads, newest first. `protected` ones are listed only to say so. */
  items(tabId) {
    return [...(this.byTab.get(tabId)?.values() ?? [])]
      .filter((f) => f.status === 'ok')
      .reverse()
      .map((f) => ({
        url: f.url, size: f.size, format: f.format, referrer: f.referrer,
        kind: f.info?.kind ?? 'unknown', maxHeight: f.info?.height ?? 0, protected: !!f.info?.encrypted,
      }));
  }

  find(tabId, url) {
    return this.items(tabId).find((f) => f.url === url);
  }

  /** For diagnosis: every file seen on the tab and what the look at its first bytes said. */
  describe(tabId) {
    return [...(this.byTab.get(tabId)?.values() ?? [])].map((f) => ({ url: f.url.split('?')[0], size: f.size, status: f.status, format: f.format ?? null }));
  }

  clear(tabId) {
    if (this.byTab.delete(tabId)) this.onChange(tabId);
  }
}
